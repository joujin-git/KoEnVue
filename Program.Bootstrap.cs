using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.UI;
using KoEnVue.Core.Native;
using KoEnVue.Core.Logging;

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
        // 메인 윈도우 클래스
        Logger.Debug("Registering main window class");
        var mainClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            lpszClassName = MainClassName,
        };
        ushort mainAtom = User32.RegisterClassExW(ref mainClass);
        if (mainAtom == 0)
            Logger.Error($"RegisterClassExW failed for main class: error={Marshal.GetLastPInvokeError()}");
        else
            Logger.Debug($"Main window class registered: atom={mainAtom}");

        // 오버레이 윈도우 클래스
        Logger.Debug($"Registering overlay window class: {_config.Advanced.OverlayClassName}");
        var overlayClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            lpszClassName = _config.Advanced.OverlayClassName,
        };
        ushort overlayAtom = User32.RegisterClassExW(ref overlayClass);
        if (overlayAtom == 0)
            Logger.Error($"RegisterClassExW failed for overlay class: error={Marshal.GetLastPInvokeError()}");
        else
            Logger.Debug($"Overlay window class registered: atom={overlayAtom}");
    }

    private static IntPtr CreateMainWindow()
    {
        IntPtr hwnd = User32.CreateWindowExW(
            0, MainClassName, "KoEnVue",
            0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            Logger.Error("Failed to create main window");

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

    // ================================================================
    // 종료 처리
    // ================================================================

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        _stopping = true;

        // 1. IME 훅 해제
        ImeStatus.UnregisterHook();

        // 1a. 세션 알림 해제 — DestroyWindow 전에 풀어야 wtsapi32 내부 핸들 매핑이 깔끔히 정리됨.
        if (_hwndMain != IntPtr.Zero)
            Wtsapi32.WTSUnRegisterSessionNotification(_hwndMain);

        // 2. CAPS LOCK 폴링 타이머 명시적 해제
        if (_hwndMain != IntPtr.Zero)
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_CAPS);

        // 3. 트레이 아이콘 제거 — 내부의 StopAddRetryTimer 가 KillTimer(_hwndMain, …) 를 호출하므로
        //    DestroyWindow 전에 실행해 죽은 hwnd 에 Win32 호출이 나가는 걸 방지.
        //    NIM_DELETE 자체는 NIF_GUID 기반이라 hwnd 유효성과 무관하지만 타이머 정리 경로가 있음.
        Tray.Remove();

        // 4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
        Animation.Dispose();
        Overlay.Dispose();

        // 5. 오버레이 + 메인 윈도우 파괴
        if (_hwndOverlay != IntPtr.Zero)
            User32.DestroyWindow(_hwndOverlay);
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
