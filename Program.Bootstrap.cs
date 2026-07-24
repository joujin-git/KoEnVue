using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
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
    }

    /// <summary>
    /// 중복 실행 시 기존(실행 중) 인스턴스를 찾아 활성화 신호를 전송한다.
    /// 메인 윈도우 클래스명으로 <c>FindWindowW</c> 탐색 → <c>PostMessageW</c> 로
    /// <see cref="AppMessages.WM_APP_ACTIVATE"/> 게시. 기존 인스턴스는 WndProc 에서 이를 받아
    /// 인디케이터를 즉시 표시한다.
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
        Win32DialogHelper.RegisterStandardClass(
            _config.Advanced.OverlayClassName,
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
            _config.Advanced.OverlayClassName, "",
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
    /// foreground → 플로팅 배지 SystemFilter hide 회귀. cursor 윈도우는 생성 시 일반 z-order 로 시작,
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
            _config.Advanced.OverlayClassName, "",
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

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        _stopping = true;

        // 0. 감지 스레드 합류 — _stopping=true 신호 후 한 폴링 주기 안에 자발 종료한다.
        //    IsBackground=true 라 OS 가 어차피 강제 종료하지만, hwnd 파괴 시점과
        //    detection 의 PostMessageW(_hwndMain, ...) marshal 이 겹치는 짧은 race
        //    (last-error=1400 / Invalid handle) 를 명시적 Join 으로 차단.
        _detectionThread?.Join(500);

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
        Tray.Remove();

        // 4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
        Animation.Dispose();
        Overlay.Dispose();
        CursorOverlay.Dispose();

        // 5. 오버레이 + 메인 윈도우 파괴
        if (_hwndOverlay != IntPtr.Zero)
            User32.DestroyWindow(_hwndOverlay);
        if (_hwndCursorOverlay != IntPtr.Zero)
            User32.DestroyWindow(_hwndCursorOverlay);
        if (_hwndMain != IntPtr.Zero)
            User32.DestroyWindow(_hwndMain);

        // 6. Mutex 해제 (Dispose만 — 프로세스 종료 시 OS가 자동 해제.
        //    ReleaseMutex는 소유 스레드에서만 호출 가능하나 ProcessExit는 다른 스레드일 수 있음)
        _mutex?.Dispose();

        // 7. 로거 종료 (Shutdown 전에 최종 로그 기록)
        //    COM 해제는 [STAThread] 로 CLR 이 메인 스레드 종료 시 자동 수행하므로 여기서 부르지 않는다.
        //    ProcessExit 는 finalizer 스레드에서 돌아 메인 스레드의 apartment 와 매칭되지도 않는다.
        Logger.Info("KoEnVue stopped");
        Logger.Shutdown();
    }
}
