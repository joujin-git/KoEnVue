using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.Messaging;
using KoEnVue.App.UI;
using KoEnVue.Core.Native;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;

namespace KoEnVue;

/// <summary>
/// Program 의 부트스트랩/종료 분할.
/// MainImpl 의 보조 단계(다중 인스턴스 가드, 트레이 잔재 청소, 윈도우 클래스/핸들 생성)
/// 와 ProcessExit 정리 시퀀스를 모아둔다.
/// 메시지 루프/이벤트 핸들러/감지 스레드는 <c>Program.cs</c> 본체에 그대로 둔다.
/// </summary>
internal static partial class Program
{
    // ================================================================
    // 부트스트랩 전용 상태
    // ================================================================

    private static Mutex? _mutex;

    // TaskbarCreated 브로드캐스트 메시지 ID — Explorer 재시작 시 셸이 모든 최상위 창에 브로드캐스트.
    // RegisterWindowMessageW 의 반환값 (0이면 등록 실패 → 핸들러 비활성).
    // 동적 ID 라 WndProc switch 에 넣지 못하므로 switch 앞단의 if 분기에서 비교한다.
    private static uint _taskbarCreatedMsgId;

    /// <summary>
    /// 부팅 시 <see cref="RegisterWindowClasses"/> 가 실제로 등록한 오버레이 클래스명.
    /// 창 생성은 <c>_config.Advanced.OverlayClassName</c> 이 아니라 이 값을 써야 한다 —
    /// 클래스 등록은 1회뿐이라 핫리로드로 config 값이 바뀌면 둘이 어긋나고, 그 상태에서
    /// 창을 만들면 미등록 클래스로 실패한다 (AUDIT-2026-07-30 §H).
    /// </summary>
    private static string _registeredOverlayClassName = DefaultConfig.DefaultOverlayClassName;

    /// <summary>
    /// 종료 시 감지 스레드 합류 대기 상한 (ms). 감지 루프의 최악 sleep
    /// (<c>MaxPollMs</c> + <c>DetectionBackoffMaxMs</c>)보다 <b>짧다</b> — 의도적이다.
    /// 근거는 <c>OnProcessExit</c> 의 주석 참조 (AUDIT-2026-07-30 §I).
    /// </summary>
    private const int DetectionJoinTimeoutMs = 500;

    // ================================================================
    // 다중 인스턴스 방지
    // ================================================================

    private static bool TryAcquireMutex()
    {
        try
        {
            _mutex = new Mutex(true, DefaultConfig.MutexName, out bool createdNew);
            if (!createdNew)
            {
                Logger.Info("Another instance already running, exiting");
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }
        catch (AbandonedMutexException ex)
        {
            // 이전 인스턴스가 비정상 종료 → WAIT_ABANDONED.
            // .NET 런타임이 우리 호출자에게 자동으로 소유권을 넘긴 상태이므로 createdNew=true 와
            // 동일하게 진행한다. 캐치 시점에 _mutex 필드 set 여부는 런타임 버전마다 다르므로
            // null 인 경우만 재취득 (이미 우리가 소유한 named mutex 라 즉시 반환).
            Logger.Warning($"Mutex was abandoned by previous crashed instance: {ex.Message}");
            _mutex ??= new Mutex(true, DefaultConfig.MutexName, out _);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // High IL 인스턴스가 보유한 mutex 에 Medium 2nd instance 가 접근 거부되는 경우
            // (오딧 M1). 소유권은 얻지 못하지만 기존 인스턴스 활성화 신호는 보낼 수 있다 —
            // 호출부가 NotifyExistingInstance() 를 이어서 호출한다.
            Logger.Warning($"Mutex inaccessible (likely integrity-level mismatch): {ex.Message}");
            _mutex?.Dispose();
            _mutex = null;
            return false;
        }
    }

    /// <summary>
    /// 중복 실행 시 기존(실행 중) 인스턴스를 찾아 활성화 신호를 전송한다.
    /// 메인 윈도우 클래스명으로 <c>FindWindowW</c> 탐색 → <c>PostMessageW</c> 로
    /// <see cref="AppMessages.WM_APP_ACTIVATE"/> 게시. 기존 인스턴스는 WndProc 에서 이를 받아
    /// 한/영 배지를 즉시 표시한다.
    /// 탐색 실패(기존 창이 막 파괴 중이거나 클래스명이 달라진 경우)는 조용히 무시한다.
    /// </summary>
    private static void NotifyExistingInstance()
    {
        IntPtr hwndExisting = User32.FindWindowW(MainClassName, null);
        if (hwndExisting == IntPtr.Zero)
        {
            Logger.Debug("NotifyExistingInstance: no existing window found");
            return;
        }

        if (!User32.PostMessageW(hwndExisting, AppMessages.WM_APP_ACTIVATE, IntPtr.Zero, IntPtr.Zero))
            Logger.Warning("NotifyExistingInstance: PostMessageW failed");
        else
            Logger.Debug("NotifyExistingInstance: WM_APP_ACTIVATE posted to existing instance");
    }

    // ================================================================
    // 크래시 복구 — 고정 GUID NIM_DELETE
    // ================================================================

    private static unsafe void CleanupPreviousTrayIcon()
    {
        NOTIFYICONDATAW nid = default;
        nid.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        nid.uFlags = Win32Constants.NIF_GUID;
        nid.guidItem = DefaultConfig.AppGuid;
        Shell32.Shell_NotifyIconW(Win32Constants.NIM_DELETE, ref nid);
    }

    // ================================================================
    // 윈도우 클래스 등록 + 윈도우 생성
    // ================================================================

    private static unsafe void RegisterWindowClasses()
    {
        // 메인 윈도우 — 메시지 전용 0×0 hidden, 호버 대상 아님. hbrBackground 는 NULL 그대로.
        Win32DialogHelper.RegisterStandardClass(
            MainClassName,
            (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc);

        // 오버레이 — WS_EX_LAYERED 라 WM_ERASEBKGND 미수신, hbrBackground 는 NULL 그대로.
        // hCursor=IDC_ARROW 가 필수: NULL 이면 drag_modifier ≠ none 모드의 평상시(HTCLIENT 반환)
        // 호버에 IDC_APPSTARTING 폴백이 노출됨. RegisterStandardClass 가 이를 자동 박는다.
        //
        // **등록한 이름을 고정**한다 (AUDIT-2026-07-30 §H). 클래스 등록은 부팅 시 1회뿐인데
        // overlay_class_name 은 핫리로드로 바뀔 수 있고, 커서 헤일로 창은 lazy 생성이라 그 이후에
        // 만들어진다 — 창 생성이 _config 를 직접 읽으면 **등록된 적 없는 클래스명**으로
        // CreateWindowExW 를 호출해 실패한다(헤일로가 영영 안 뜬다). 창 생성은 반드시 이 필드를 쓴다.
        _registeredOverlayClassName = _config.Advanced.OverlayClassName;
        Win32DialogHelper.RegisterStandardClass(
            _registeredOverlayClassName,
            (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc);
    }

    private static IntPtr CreateMainWindow()
    {
        IntPtr hwnd = User32.CreateWindowExW(
            0, MainClassName, "KoEnVue",
            0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastPInvokeError();
            Logger.Error($"Failed to create main window: error={err}");
        }

        return hwnd;
    }

    private static IntPtr CreateOverlayWindow()
    {
        IntPtr hwnd = User32.CreateWindowExW(
            Win32Constants.WS_EX_LAYERED
                | Win32Constants.WS_EX_TOPMOST | Win32Constants.WS_EX_TOOLWINDOW
                | Win32Constants.WS_EX_NOACTIVATE,
            _registeredOverlayClassName, "",
            Win32Constants.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            Logger.Error("Failed to create overlay window");

        return hwnd;
    }

    /// <summary>
    /// 커서 헤일로 전용 별도 HWND. 메인 overlay 와 동일 클래스 (한 클래스가 두 WS_POPUP 인스턴스
    /// 를 가질 수 있음) + WS_EX_TRANSPARENT 추가로 마우스 hit-test 통과 보장 (커서 헤일로 위 클릭이
    /// 아래 창으로 자연 통과). dev-notes/2026-05-15-click-through-attempts.md F2: WS_EX_TRANSPARENT
    /// 영구 ON 이 OS 차원에서 유일한 신뢰 가능 클릭 통과 방식.
    /// <para>
    /// <b>WS_EX_TOPMOST 생성 시 제거</b> — 진단 결과 cursor 윈도우 첫 UpdateLayeredWindow 가 DWM 합성
    /// 시 다른 topmost 윈도우 (Shell_TrayWnd 도 topmost) 재정렬 trigger → Shell_TrayWnd 잠시
    /// foreground → 한/영 배지 SystemFilter hide 회귀. cursor 윈도우는 생성 시 일반 z-order 로 시작,
    /// 첫 표시 (RenderAtCursor) 시점에 명시 SetWindowPos(HWND_TOPMOST, SWP_NOSENDCHANGING) 으로
    /// topmost 진입 — 다른 윈도우에 z-order 변경 알림 차단.
    /// </para>
    /// </summary>
    private static IntPtr CreateCursorOverlayWindow()
    {
        // WS_VISIBLE 처음부터 박음 — 이후 ShowWindow 호출이 일체 없어야 z-order 변경 0 → 트레이
        // 메뉴 modal loop 안에서 cursor 가 표시되어도 메뉴 dismiss 트리거 안 함.
        // WS_EX_TOPMOST 의도적 제거 (위 주석 참조) — CursorOverlay.RenderAtCursor 가 첫 표시 시 명시 set.
        IntPtr hwnd = User32.CreateWindowExW(
            Win32Constants.WS_EX_LAYERED
                | Win32Constants.WS_EX_TOOLWINDOW
                | Win32Constants.WS_EX_NOACTIVATE | Win32Constants.WS_EX_TRANSPARENT,
            _registeredOverlayClassName, "",
            Win32Constants.WS_POPUP | Win32Constants.WS_VISIBLE,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            Logger.Error("Failed to create cursor overlay window");

        return hwnd;
    }

    // ================================================================
    // 종료 처리
    // ================================================================

    /// <summary>
    /// <c>ProcessExit</c> 핸들러를 등록한 스레드(= 메인). 아래 스레드 친화성 실측용 (확정 #45).
    /// <b>실측 완료 (2026-08-02)</b> — 두 종료 경로 모두 <c>sameThread=True</c>. 계측은 종료 경로가
    /// 바뀌었을 때 같은 판정을 다시 할 수 있도록 남긴다.
    /// </summary>
    private static int _mainThreadId;

    /// <summary>
    /// 창을 파괴하고 결과를 남긴다 — 세 갈래(skip / ok / failed)를 모두 찍는 이유는 무로그를
    /// "성공" 으로 읽으면 조기 반환과 구분되지 않아 실측이 성립하지 않기 때문이다. <c>DestroyWindow</c>
    /// 는 <b>창을 만든 스레드에서만</b> 성공하므로 이 로그가 곧 <c>OnProcessExit</c> 의 스레드 친화성
    /// 실측이었다 (확정 #45 — 2026-08-02 확정, <c>ok</c> 3회).
    ///
    /// <para>
    /// <b>필드 대입은 호출자가 직접 한다</b> — 세 핸들 필드는 감지 스레드가 읽는 <c>volatile</c> 인데
    /// <c>ref</c> 로 넘기면 CS0420 대로 volatile 보장이 사라져, §N-42 가 세운 "다른 스레드가 즉시
    /// Zero 를 관측하고 자기 가드에 걸린다" 는 전제가 깨진다. 헬퍼는 파괴와 로그만 맡는다.
    /// </para>
    /// </summary>
    private static void DestroyWindowLogged(IntPtr hwnd, string name)
    {
        // 세 갈래를 모두 남긴다 — 무로그를 "성공" 으로 읽으면 조기 반환과 구분되지 않아 실측이
        // 성립하지 않는다. skip 은 WM_DESTROY 가 먼저 내린 정상 경로의 흔적이기도 하다 (G5).
        if (hwnd == IntPtr.Zero)
        {
            Logger.Debug($"DestroyWindow({name}) skipped: already reset by WM_DESTROY");
            return;
        }

        if (User32.DestroyWindow(hwnd))
            Logger.Debug($"DestroyWindow({name}) ok");
        else
            Logger.Debug($"DestroyWindow({name}) failed: error={Marshal.GetLastPInvokeError()}");
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        _stopping = true;

        // 스레드 친화성 (확정 #45) — **2026-08-02 실측으로 확정**: 두 종료 경로(트레이 「종료」의
        // PostQuitMessage, 트레이 「관리자 권한」 토글의 WM_CLOSE) 모두 sameThread=True 이고
        // DestroyWindow 는 ok 를 돌려줬다. 즉 이 핸들러는 메인 스레드에서 돈다. 계측은 남긴다 —
        // 종료 경로가 바뀌면 같은 판정을 다시 해야 한다.
        int exitThreadId = Environment.CurrentManagedThreadId;
        Logger.Debug($"OnProcessExit: threadId={exitThreadId}, mainThreadId={_mainThreadId}, "
                     + $"sameThread={exitThreadId == _mainThreadId}");

        // 0. 감지 스레드 합류 — _stopping=true 신호 후 자발 종료를 기다린다. hwnd 파괴 시점과
        //    detection 의 PostMessageW(_hwndMain, ...) 가 겹치는 race(last-error=1400 / Invalid
        //    handle) 를 좁히기 위함이다.
        //
        //    **"차단한다" 고 쓰면 사실과 다르다** (AUDIT-2026-07-30 §I). 감지 루프의 최악 sleep 은
        //    PollIntervalMs(최대 DefaultConfig.MaxPollMs) + 예외 백오프(최대
        //    DefaultConfig.DetectionBackoffMaxMs) 라 이 타임아웃을 넘길 수 있다 — 즉 백오프 중
        //    종료하면 Join 이 타임아웃하고 race 창이 그대로 열린다.
        //
        //    그럼에도 타임아웃을 늘리지 않는 이유: 스레드가 IsBackground=true 라 OS 가 어차피
        //    회수하고, race 가 실제로 터져도 결과는 PostMessageW 실패(로그 한 줄)뿐이다. 종료를
        //    수 초 지연시키는 대가가 이득보다 크다. 대신 타임아웃을 관측 가능하게 남긴다.
        if (_detectionThread is not null && !_detectionThread.Join(DetectionJoinTimeoutMs))
        {
            Logger.Debug(
                $"Detection thread did not exit within {DetectionJoinTimeoutMs}ms "
                + "(likely mid-backoff); proceeding with teardown");
        }

        // 1. IME 훅 해제
        ImeStatus.UnregisterHook();

        // 1a. 세션 알림 해제 — DestroyWindow 전에 풀어야 wtsapi32 내부 핸들 매핑이 깔끔히 정리됨.
        if (_hwndMain != IntPtr.Zero)
            Wtsapi32.WTSUnRegisterSessionNotification(_hwndMain);

        // 2. CAPS LOCK 폴링 타이머 명시적 해제
        if (_hwndMain != IntPtr.Zero)
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_CAPS);

        // 2a. 커서 헤일로 모션 폴링 타이머 명시적 해제 (활성 중일 때만 등록되어 있음)
        if (_hwndMain != IntPtr.Zero)
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_CURSOR_MOTION);

        // 3. 트레이 아이콘 제거 — 내부의 StopAddRetryTimer 가 KillTimer(_hwndMain, …) 를 호출하므로
        //    DestroyWindow 전에 실행해 죽은 hwnd 에 Win32 호출이 나가는 걸 방지.
        //    NIM_DELETE 자체는 NIF_GUID 기반이라 hwnd 유효성과 무관하지만 타이머 정리 경로가 있음.
        Tray.Remove(TrayRemoveReason.Shutdown);

        // 4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
        Animation.Dispose();
        Overlay.Dispose();
        CursorOverlay.Dispose();

        // 5. 오버레이 + 메인 윈도우 파괴.
        //    **파괴 직후 핸들 필드를 즉시 비운다** (AUDIT-2026-07-30 §N-42). 이전에는 필드가 죽은
        //    핸들을 계속 들고 있어, Join 이 타임아웃한 감지 스레드(§I)나 늦게 도착한 콜백이
        //    PostMessageW/SetWindowPos 를 죽은 hwnd 에 계속 보냈다. 세 필드 모두 volatile 이라
        //    다른 스레드가 즉시 Zero 를 관측하고 자기 가드(`!= IntPtr.Zero`)에 걸린다.
        //
        //    **여기가 유일한 리셋 지점은 아니다** — WM_DESTROY 핸들러(Program.WndProcCore)도 같은
        //    리셋을 한다. 창이 이 지점보다 **먼저** 죽는 경로(트레이 「관리자 권한」 토글이 보내는
        //    WM_CLOSE)가 있고, 그때는 아래 가드가 이미 Zero 를 보고 건너뛴다 — 이중 파괴도 함께
        //    막힌다 (bug-hunt 2026-08-02 확정 #10·#40·#43).
        //    반환값을 로그로 남긴다 — 이 함수가 창을 만든 스레드가 아니라면 Win32 가 파괴를 거부하고
        //    false 를 돌려준다. 위 threadId 로그와 짝을 이뤄 단계 7 의 전제를 가른 실측이다 (#45).
        //    **2026-08-02 실측**: 트레이 「종료」 경로는 세 창 모두 ok, WM_CLOSE 경로는 _hwndMain 만
        //    skipped(= WM_DESTROY 가 먼저 내렸다) — G5 가 의도한 동작이 그대로 관측됐다.
        DestroyWindowLogged(_hwndOverlay, nameof(_hwndOverlay));
        _hwndOverlay = IntPtr.Zero;
        DestroyWindowLogged(_hwndCursorOverlay, nameof(_hwndCursorOverlay));
        _hwndCursorOverlay = IntPtr.Zero;
        DestroyWindowLogged(_hwndMain, nameof(_hwndMain));
        _hwndMain = IntPtr.Zero;

        // 6. Mutex 해제 (Dispose만 — 프로세스 종료 시 OS가 자동 해제.
        //    ReleaseMutex는 소유 스레드에서만 호출 가능하나 ProcessExit는 다른 스레드일 수 있음)
        _mutex?.Dispose();

        // 7. 로거 종료 (Shutdown 전에 최종 로그 기록)
        //    COM 해제는 [STAThread] 로 CLR 이 메인 스레드 종료 시 자동 수행하므로 여기서 부르지 않는다.
        //
        //    **이전 주석의 "ProcessExit 는 finalizer 스레드에서 돈다" 는 단언은 2026-08-02 실측으로
        //    반증됐다** (확정 #45) — 두 종료 경로 모두 sameThread=True 이고 DestroyWindow 는 ok 다.
        //    이 핸들러는 메인 스레드에서 돌므로 단계 2·2a 의 KillTimer 와 단계 5 의 DestroyWindow 는
        //    유효하고, §N-42 의 "파괴 직후 필드를 Zero" 도 실제로 파괴된 창을 지운다.
        //    COM 해제를 여기서 부르지 않는 것은 그대로 옳다 — 다만 근거는 "다른 스레드라 매칭이 안
        //    된다" 가 아니라 **[STAThread] 로 CLR 이 자동 수행하므로 불필요**하다는 것이다.
        //    (관측 로그: OnProcessExit: threadId=1, mainThreadId=1, sameThread=True)
        Logger.Info("KoEnVue stopped");
        Logger.Shutdown();
    }
}
