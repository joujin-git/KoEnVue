using System.Runtime.InteropServices;
using KoEnVue.App.Bootstrap;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
using KoEnVue.App.Startup;
using KoEnVue.App.Messaging;
using KoEnVue.App.UI;
using KoEnVue.App.Update;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue;

/// <summary>
/// 앱 진입점 + Win32 메시지 루프 + 2-스레드 모델 + 이벤트 파이프라인.
///
/// <para>
/// 가독성을 위해 partial class 분할:
/// <list type="bullet">
///   <item><c>Program.cs</c> — 진입점, MainImpl, 메시지 루프, WndProc, 표시/IME/포커스, 감지 스레드 기동(<see cref="DetectionService"/> 위임)</item>
///   <item><c>Program.Bootstrap.cs</c> — 다중 인스턴스, 윈도우 클래스/생성, ProcessExit</item>
///   <item><c>Program.OverlayDrag.cs</c> — 플로팅 배지 클릭 숨김·드래그 승격·위치 저장</item>
///   <item><c>Program.SystemEvents.cs</c> — 전원/디스플레이/테마/DPI/세션/TaskbarCreated</item>
///   <item><c>Program.Timers.cs</c> — WM_TIMER 위임, CAPS 폴링, 커서 헤일로 lifecycle</item>
/// </list>
/// </para>
/// </summary>
internal static partial class Program
{
    // ================================================================
    // 전역 상태
    // ================================================================

    // 윈도우 핸들
    // 모두 cross-thread access — 메인 스레드 write (CreateMainWindow / CreateOverlayWindow /
    // CreateCursorOverlayWindow) vs 감지 스레드 read (PostMessageW, IsKoenvueWindow). x64 TSO 에서는
    // 단일 init-then-read 패턴 덕에 회귀 0 이지만, ARM64 weak memory model 회귀 방어용 volatile.
    private static volatile IntPtr _hwndMain;
    private static volatile IntPtr _hwndOverlay;
    // 커서 헤일로 전용 별도 HWND. config.CursorIndicatorEnabled = false 면 IntPtr.Zero — lazy 생성
    // 패턴 (HandleConfigChanged 의 OFF→ON 분기에서 첫 생성). 메인 _hwndOverlay 와 같은 클래스이나
    // WS_EX_TRANSPARENT 가 추가로 박힌다 (Program.Bootstrap.CreateCursorOverlayWindow).
    private static volatile IntPtr _hwndCursorOverlay;

    // 스레드 간 공유 상태 (volatile — 원자적 참조/값 교체)
    private static volatile AppConfig _config = null!;
    private static volatile ImeState _lastImeState = ImeState.English;
    private static volatile bool _indicatorVisible;

    // 플로팅 배지 좌클릭 일시 숨김 — UserHidden 과 무관. 포커스 변경 / 한·영(IME) 변경 시
    // HandleFocusChanged·HandleImeStateChanged 가 클리어하며 재표시. 메인 스레드 전용.
    private static bool _clickDismissed;

    // 오버레이 좌버튼 캡처 중 드래그 승격 상태 — 메인 스레드 전용 (WndProc).
    // pending=true 이면 LBUTTONDOWN 이후 업/승격 대기. promoted=true 이면 HTCAPTION 드래그로 넘김.
    private static bool _overlayDragPending;
    private static bool _overlayDragPromoted;
    private static int _overlayDragOriginX;
    private static int _overlayDragOriginY;

    // 포그라운드 윈도우 + 앱별 위치 (메인 스레드 전용)
    private static IntPtr _lastForegroundHwnd;
    private static string _currentProcessName = "";
    private static readonly Dictionary<IntPtr, (int x, int y)> _hwndPositions = [];

    // CAPS LOCK 토글 캐시 (메인 스레드 전용 — TIMER_ID_CAPS 폴러가 200ms마다 GetKeyState 비교)
    private static bool _lastCapsLockState;

    // UpdateChecker 백그라운드 스레드 → 메인 스레드 페이로드 전달.
    // PostMessage 의 wParam/lParam 으로 객체를 직접 못 보내므로 volatile 참조로 게시한다.
    private static volatile UpdateInfo? _pendingUpdate;

    // 라이프사이클 (감지 스레드에서 읽고 OnProcessExit에서 씀 → volatile)
    private static volatile bool _stopping;

    // 감지 스레드 참조 — OnProcessExit 가 _stopping=true 신호 후 Join(500) 으로 합류해
    // hwnd 파괴와 PostMessageW(_hwndMain, ...) 가 겹치는 짧은 race window 를 차단한다.
    private static Thread? _detectionThread;

    // 세션 잠금 상태 — WM_WTSSESSION_CHANGE 핸들러(메인 스레드)가 쓰고 감지 스레드가 읽음.
    // HideOnLockScreen 이 켜져 있고 이 플래그가 true 이면 감지 루프가 한 틱을 skip 해서
    // LogonUI 가 필터를 뚫어도 인디가 다시 켜지지 않도록 보장한다.
    private static volatile bool _sessionLocked;

    // 윈도우 클래스명 (P3: 매직 스트링 금지)
    private const string MainClassName = "KoEnVueMain";

    // 설정 파일명 → DefaultConfig에서 참조

    // ================================================================
    // 진입점
    // ================================================================

    [STAThread]
    static void Main()
    {
        try
        {
            MainImpl();
        }
        catch (Exception ex)
        {
            AppendCrashFile("FATAL", ex);
            Logger.Error($"Fatal: {ex}");
            Logger.Shutdown();
        }
    }

    /// <summary>
    /// PR-10 (G5): 메인 스레드 외 unhandled / unobserved 예외를 흡수해 <c>koenvue_crash.txt</c> +
    /// <c>koenvue.log</c> 양쪽에 흔적을 남기고 종료한다. <c>AppDomain.UnhandledException</c> 은
    /// CLR 이 프로세스를 죽이기 직전 호출되며 (<c>IsTerminating=true</c>), 핸들러 안에서 GUI 호출은
    /// thread affinity 문제로 금지 — <c>Logger.Error</c> 와 파일 write 만 사용. AppDomain 핸들러는
    /// background 스레드 + 메인 스레드 양쪽의 미흡수 예외를 모두 받는다.
    /// </summary>
    private static void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            object exObj = e.ExceptionObject;
            AppendCrashFile("UNHANDLED", exObj);
            Logger.Error($"UnhandledException (terminating={e.IsTerminating}): {exObj}");
            // FailFast / AVE 등으로 ProcessExit 가 발화하지 않는 경로에서 트레이 좀비 아이콘이
            // 남는 회귀를 차단. NIM_DELETE 는 NIF_GUID 기반이라 hwnd / 스레드 affinity 무관 +
            // bool 반환 ignored (이미 종료 경로). 다음 부팅의 CleanupPreviousTrayIcon
            // 자기치유는 그대로 유지 — 본 호출은 사용자가 즉시 재실행할 때를 위한 best-effort.
            CleanupPreviousTrayIcon();
            Logger.Shutdown();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppendCrashFile("UNOBSERVED", e.Exception);
            Logger.Error($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();  // finalizer 가 프로세스를 죽이지 않도록 관측 표시.
        };
    }

    /// <summary>
    /// 비상 크래시 로그 파일에 한 줄 append. Logger 초기화 전에도 동작한다.
    /// I/O · 권한 · 보안 실패는 흡수 — 이미 종료 경로라 추가 복구 불가. 로직 버그는 전파.
    ///
    /// <para>
    /// PR-15: <c>internal</c> 로 노출 — <c>App/Bootstrap/AdminElevation</c> 가
    /// pre-Init elevation 로그 (Logger.Initialize 전 ShellExecute+Exit 흐름에서
    /// pre-Init 버퍼가 flush 안 되는 경우) 의 crash.txt fallback 채널로 재사용.
    /// 태그는 elevation 흐름의 의미 (ELEVATION / ELEVATION-ERR) — 본래 크래시용
    /// 태그 (FATAL / UNHANDLED / UNOBSERVED) 와 grep 으로 분리 가능.
    /// </para>
    /// </summary>
    internal static void AppendCrashFile(string tag, object payload)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "koenvue_crash.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {tag}: {payload}\n");
        }
        catch (Exception inner) when (inner is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            _ = inner;
        }
    }

    static void MainImpl()
    {
        // 0. Core 로깅 sink 배선 — Core 코드의 `LogProvider.Sink?.X(...)` 호출이 Logger 로 흐르도록.
        //    Logger.Initialize 가 호출되기 전에 Core 코드(예: Settings.Load 내부 JsonSettingsManager)
        //    가 sink 를 통해 보낸 메시지는 Logger 의 pre-Initialize 버퍼에 쌓였다가 Initialize 직후
        //    한꺼번에 koenvue.log 로 flush — PR-06 Tier-3 ④ 에서 발견된 Trace-only 한계 해소.
        LogProvider.Sink = new LoggerSink();

        // 0a. AppDomain unhandled + Task unobserved 예외 핸들러 (PR-10, G5).
        //     background 스레드 (DetectionService.RunLoop / Logger drain / UpdateChecker / StartupTaskManager)
        //     의 outer catch 가 흡수하지 못한 예외 — 주로 NullReferenceException 등 로직 버그 —
        //     를 koenvue_crash.txt 에 박제. Logger.Error 도 pre-Init 버퍼 경유로 안전.
        RegisterCrashHandlers();

        // 0b. 설정 로드 — mutex 획득 전 (PR-15). admin_elevation 옵션을 자기 IL / 재진입 가드
        //     와 함께 검사하려면 config 가 먼저 있어야 한다. Settings.Load 내부의 Logger.Warning
        //     등은 LogProvider.Sink 의 pre-Init 버퍼 경유로 Logger.Initialize 직후 flush.
        _config = Settings.Load();

        // 0b-1. PR-15 후속 fix — Tray 메뉴 토글 재시작 + self-elevation 손자 spawn 경로에서
        //       부모 종료를 명시 대기. 환경변수 KOENVUE_RELAUNCH_PARENT_PID 가 set 돼 있을 때만
        //       동작 (정상 부팅에는 noop). mutex / trayicon GUID / WTS notification 등 race 차단.
        AdminElevation.WaitForRelaunchParentIfAny();

        // 0c. admin_elevation 처리 (PR-15) — UIPI 우회용 self-elevation.
        //     mutex 획득 전 호출 — 원본이 mutex 안 잡은 상태라 자식 (High IL) 이 깨끗하게 새로
        //     createdNew=true 획득 (race 0). ExitForChild = 원본 즉시 종료 (자식 spawn 성공).
        //     Continue / ContinueAfterDenied = 일반 권한으로 계속 (옵션 비활성 / 이미 High IL /
        //     재진입 가드 트립 / UAC 거부 / ShellExecuteW 실패 — 모든 거부 시 사용자 알림 후 진행).
        if (AdminElevation.TryRelaunchAsAdmin(_config) == AdminElevation.Result.ExitForChild)
            return;

        // 1. 다중 인스턴스 체크 — 실패 시 기존 인스턴스에 활성화 신호만 보내고 즉시 종료.
        //    Cleanup 보다 먼저 실행해야 "이미 실행 중" 인 정상 인스턴스의 트레이 아이콘을
        //    NIM_DELETE 로 지워버리는 부작용이 없다.
        if (!TryAcquireMutex())
        {
            NotifyExistingInstance();
            return;
        }

        // 2. 이전 트레이 찌꺼기 정리 — Mutex 획득 성공했으므로 동일 GUID 로 남은 아이콘은
        //    이전 크래시의 유령이다.
        CleanupPreviousTrayIcon();

        // (설정 로드는 PR-15 에서 단계 0b 로 이동 — mutex 전 admin_elevation 검사 위해 선행 필수.)

        // 4. 로거 + I18n 초기화
        //    asInvoker 전환 (PR-03) 후 log_file_path 는 PortablePath.SanitizeLogPath 가 허용 루트
        //    (BaseDirectory / %LOCALAPPDATA%\KoEnVue) 외 값을 거부. 거부 사유는 Logger.Initialize 이후
        //    reissue 해야 koenvue.log 에도 남는다 (Trace 만 남는 PR-01 패턴과 동일).
        Logger.SetLevel(_config.LogLevel);
        string resolvedLogPath = PortablePath.SanitizeLogPath(_config.LogFilePath, out string? logPathReject);
        Logger.Initialize(_config.LogToFile, resolvedLogPath, _config.LogMaxSizeMb);
        if (logPathReject is not null)
            Logger.Warning($"{logPathReject}; using '{resolvedLogPath}'");

        Logger.Debug($"Config: TrayEnabled={_config.TrayEnabled}, DisplayMode={_config.DisplayMode}, EventDisplayDurationMs={_config.EventDisplayDurationMs}, PollIntervalMs={_config.PollIntervalMs}");
        I18n.Load(_config.Language);
        Logger.Info("KoEnVue starting");

        // 5. 메인 스레드 COM STA 는 [STAThread] 로 CLR 이 Main 진입 전에 CoInitializeEx 를 부른
        //    상태로 보장된다 (종료 시 CoUninitialize 짝 호출도 CLR 책임). 여기서 별도 호출을 하면
        //    CLR 호출 위에 참조카운트만 쌓여 종료 경로에서 짝 맞춤이 어긋날 뿐, STA 모드 자체는
        //    이미 활성이므로 생략한다. 메시지 루프 · WinEventHook · SystemFilter VDM 모두 이 STA 를 공유.

        // 6. SystemFilter static constructor 강제 실행 (메인 스레드 STA 에서 VDM COM 생성)
        _ = SystemFilter.ShouldHide(IntPtr.Zero, IntPtr.Zero, _config);

        // 7. 윈도우 클래스 등록
        Logger.Debug("Registering window classes");
        RegisterWindowClasses();

        // 8. 메인 윈도우 생성 (메시지 전용, 화면 미표시)
        Logger.Debug("Creating main window");
        _hwndMain = CreateMainWindow();
        if (_hwndMain == IntPtr.Zero)
        {
            Logger.Error("Main window creation failed, aborting");
            return;
        }

        // 8a. Explorer 재시작 감지용 브로드캐스트 메시지 ID 등록.
        //     셸이 재시작될 때마다 모든 최상위 창에 이 메시지를 보낸다 → WndProc 에서
        //     트레이 아이콘을 재등록해 아이콘 유실을 복구.
        _taskbarCreatedMsgId = User32.RegisterWindowMessageW("TaskbarCreated");
        if (_taskbarCreatedMsgId == 0)
            Logger.Warning("RegisterWindowMessageW(TaskbarCreated) failed — Explorer-restart tray recovery disabled");
        else if (!User32.ChangeWindowMessageFilterEx(_hwndMain, _taskbarCreatedMsgId,
                     Win32Constants.MSGFLT_ALLOW, IntPtr.Zero))
        {
            // requireAdministrator(High IL) 앱은 Medium IL 인 explorer 의 TaskbarCreated
            // 브로드캐스트를 UIPI 로 차단당함. 필터 화이트리스트에 실패하면 shell 재시작 복구도
            // 무력화되고, 첫 NIM_ADD 가 레이스로 실패한 케이스(ONLOGON 등)에서 복구 불가.
            Logger.Warning($"ChangeWindowMessageFilterEx(TaskbarCreated) failed: error={Marshal.GetLastPInvokeError()}");
        }

        // 8a-2. WM_APP_ACTIVATE 도 동일 UIPI 화이트리스트에 등록. admin(High IL) 으로 실행 중인데
        //       2nd 인스턴스가 Medium IL 로 남는 경로 (admin_elevation 재실행 UAC 취소, admin 환경
        //       외부 spawn, 설정 변경 과도기) 에서 NotifyExistingInstance 의 PostMessageW(WM_APP_ACTIVATE)
        //       가 UIPI 로 차단돼 "이미 실행 중" 배지 즉시 표시 피드백이 소실된다. 화이트리스트로 복구.
        //       동일 IL(일반 사용자) 이면 무해한 no-op. 정적 상수라 RegisterWindowMessage 불요.
        if (!User32.ChangeWindowMessageFilterEx(_hwndMain, AppMessages.WM_APP_ACTIVATE,
                Win32Constants.MSGFLT_ALLOW, IntPtr.Zero))
        {
            Logger.Warning($"ChangeWindowMessageFilterEx(WM_APP_ACTIVATE) failed: error={Marshal.GetLastPInvokeError()}");
        }

        // 8b. 세션 잠금/해제 알림 등록 — HideOnLockScreen 이 동작하려면 필수.
        //     실패해도 앱 부팅은 계속 (잠금 화면 숨김만 비활성). Wtsapi32.dll 은 Windows 기본 탑재.
        if (!Wtsapi32.WTSRegisterSessionNotification(_hwndMain, Win32Constants.NOTIFY_FOR_THIS_SESSION))
            Logger.Warning($"WTSRegisterSessionNotification failed: error={Marshal.GetLastPInvokeError()}");

        // 9. 오버레이 윈도우 생성
        Logger.Debug("Creating overlay window");
        _hwndOverlay = CreateOverlayWindow();
        if (_hwndOverlay == IntPtr.Zero)
        {
            Logger.Error("Overlay window creation failed, aborting");
            return;
        }

        // 9a. 렌더링 + 애니메이션 초기화
        Logger.Debug("Initializing overlay rendering");
        Overlay.Initialize(_hwndOverlay, _config);
        Logger.Debug("Initializing animation");
        Animation.Initialize(_hwndMain, _hwndOverlay, _config);

        // 9b. 트레이 아이콘 초기화
        Tray.Initialize(_hwndMain, _lastImeState, _config);

        // 9c. 시작 프로그램 태스크 경로 동기화 (exe 이동 감지 → 재등록, 백그라운드)
        StartupTaskManager.SyncStartupPathAsync(_config);

        // 9d. CAPS LOCK 폴링 타이머 시작 (200ms, 메인 스레드)
        //     GetKeyState는 calling thread 입력 상태를 읽기 때문에 메시지 큐가 있는 메인 스레드에서만
        //     신뢰할 수 있다 → 감지 스레드(80ms 폴러) 대신 WM_TIMER로 분리. Overlay.Initialize가
        //     동일한 초기값을 _capsLockOn에 주입하므로 첫 틱에 중복 UpdateColor가 발생하지 않는다.
        _lastCapsLockState = (User32.GetKeyState(Win32Constants.VK_CAPITAL) & 1) != 0;
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_CAPS, DefaultConfig.CapsLockPollMs, IntPtr.Zero);

        // 9e. 커서 헤일로 — config.CursorIndicatorEnabled = true 일 때만 윈도우 + 엔진 + 폴링 타이머
        //     생성. false 면 비활성 — 메모리/CPU 0. HandleConfigChanged 의 OFF→ON 분기에서 lazy 생성.
        if (_config.CursorIndicatorEnabled)
            EnableCursorOverlay();

        // 10. 감지 스레드 시작
        StartDetectionThread();

        // 11. IME 이벤트 훅 등록 — WinEvent 콜백이 사용자 설정 DetectionMethod 를 존중하도록 주입.
        ImeStatus.RegisterHook(_hwndMain, _config.DetectionMethod);

        // 12. 업데이트 체크 (백그라운드 1회) — UpdateCheckEnabled=false 면 네트워크 호출 없음.
        //     hwndMain 을 로컬로 스냅샷해 lambda closure 에 캡처: UpdateChecker.CheckInBackground 는
        //     즉시 반환하고 워커 스레드가 수 초 후 콜백을 호출하므로 그 시점의 _hwndMain 는 항상 valid.
        if (_config.UpdateCheckEnabled)
        {
            IntPtr hwndForUpdate = _hwndMain;
            UpdateChecker.CheckInBackground(
                DefaultConfig.AppVersion,
                DefaultConfig.UpdateRepoOwner,
                DefaultConfig.UpdateRepoName,
                info => OnUpdateCheckResult(hwndForUpdate, info));
        }

        // 13. 종료 핸들러
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Logger.Info("Initialization complete, entering message loop");

        // 14. 메인 메시지 루프
        RunMessageLoop();
    }

    // ================================================================
    // 메인 메시지 루프
    // ================================================================

    private static void RunMessageLoop()
    {
        while (User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessageW(ref msg);
        }
    }

    // ================================================================
    // WndProc — 메시지 처리
    // ================================================================

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // [UnmanagedCallersOnly] 핸들러 예외가 unmanaged 경계(DispatchMessageW)를 넘으면 NativeAOT
        // 가 프로세스를 종료시킨다. 예상 가능한 일시 예외(Win32/COM/I/O 등)는 로깅 후 해당 메시지만
        // 스킵하고 메시지 루프를 유지한다 — 감지 스레드/콜백의 catch 정책과 대칭. 로직 버그(NullRef
        // 등)는 필터 밖이라 그대로 전파되어 AppDomain 크래시 핸들러로 표면화된다.
        try
        {
            return WndProcCore(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or COMException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            Logger.Error($"WndProc handler error (msg=0x{msg:X}): {ex}");
            return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static IntPtr WndProcCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // === 동적 메시지 ID (switch 불가) ===
        // RegisterWindowMessageW 로 런타임에 받은 TaskbarCreated ID — 등록 실패 시 0.
        // 오버레이 창도 최상위라 같은 브로드캐스트를 받으므로 메인 창에서만 처리해 중복 방지.
        if (msg != 0 && msg == _taskbarCreatedMsgId && hwnd == _hwndMain)
        {
            HandleTaskbarCreated();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            // === 커스텀 메시지 (감지 스레드 → 메인 스레드) ===

            case AppMessages.WM_IME_STATE_CHANGED:
                HandleImeStateChanged((ImeState)(int)wParam);
                return IntPtr.Zero;

            case AppMessages.WM_FOCUS_CHANGED:
                HandleFocusChanged(wParam);
                return IntPtr.Zero;

            case AppMessages.WM_POSITION_UPDATED:
                HandlePositionUpdated(wParam);
                return IntPtr.Zero;

            case AppMessages.WM_HIDE_INDICATOR:
                HideOverlay("WM_HIDE_INDICATOR");
                return IntPtr.Zero;

            case AppMessages.WM_CONFIG_CHANGED:
                HandleConfigChanged();
                return IntPtr.Zero;

            case AppMessages.WM_APP_UPDATE_FOUND:
                HandleUpdateFound();
                return IntPtr.Zero;

            case AppMessages.WM_APP_ACTIVATE:
                HandleActivateRequest();
                return IntPtr.Zero;

            // === 트레이 ===

            case AppMessages.WM_TRAY_CALLBACK:
                HandleTrayCallback(lParam);
                return IntPtr.Zero;

            // === 타이머 (애니메이션 + CAPS LOCK 폴러) ===

            case Win32Constants.WM_TIMER:
                if ((nuint)(nint)wParam == AppMessages.TIMER_ID_CAPS)
                    HandleCapsLockTimer();
                else if ((nuint)(nint)wParam == AppMessages.TIMER_ID_TRAY_ADD_RETRY)
                    Tray.HandleAddRetryTimer();
                else if ((nuint)(nint)wParam == AppMessages.TIMER_ID_CURSOR_MOTION)
                    CursorOverlay.HandleCursorMotionTimer();
                else if ((nuint)(nint)wParam == AppMessages.TIMER_ID_CURSOR_POP)
                    CursorOverlay.HandleCursorPopTimer();
                else
                    HandleTimer(wParam);
                return IntPtr.Zero;

            // === 시스템 메시지 ===

            case Win32Constants.WM_POWERBROADCAST:
                if ((uint)wParam == Win32Constants.PBT_APMRESUMESUSPEND)
                    HandlePowerResume();
                return IntPtr.Zero;

            case Win32Constants.WM_DISPLAYCHANGE:
                HandleDisplayChange();
                return IntPtr.Zero;

            case Win32Constants.WM_SETTINGCHANGE:
            case Win32Constants.WM_THEMECHANGED:
            case Win32Constants.WM_DWMCOLORIZATIONCOLORCHANGED:
                HandleSettingChange();
                return IntPtr.Zero;

            case Win32Constants.WM_DPICHANGED:
                HandleDpiChanged();
                return IntPtr.Zero;

            case Win32Constants.WM_WTSSESSION_CHANGE:
                HandleSessionChange((uint)wParam);
                return IntPtr.Zero;

            case Win32Constants.WM_COMMAND:
                HandleMenuCommand((int)wParam);
                return IntPtr.Zero;

            case Win32Constants.WM_DESTROY:
                if (hwnd == _hwndMain)
                    User32.PostQuitMessage(0);
                return IntPtr.Zero;

            // === 오버레이 드래그 / 좌클릭 일시 숨김 ===
            // HTCLIENT 고정 → SetCapture 후 임계(SM_CX/CYDRAG) 이상 + drag_modifier 통과 시
            // WM_NCLBUTTONDOWN/HTCAPTION 승격(기존 ENTER/EXIT/MOVING 재사용). 미만이면 업에서
            // 일시 숨김(_clickDismissed) — 포커스·IME 변경 시 재표시.

            case Win32Constants.WM_NCHITTEST:
                if (hwnd == _hwndOverlay)
                    return Win32Constants.HTCLIENT;
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case Win32Constants.WM_LBUTTONDOWN:
                if (hwnd == _hwndOverlay)
                {
                    BeginOverlayPointerTrack();
                    return IntPtr.Zero;
                }
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case Win32Constants.WM_MOUSEMOVE:
                if (hwnd == _hwndOverlay && _overlayDragPending && !_overlayDragPromoted)
                {
                    TryPromoteOverlayDrag(hwnd);
                    return IntPtr.Zero;
                }
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case Win32Constants.WM_LBUTTONUP:
                if (hwnd == _hwndOverlay && _overlayDragPending)
                {
                    EndOverlayPointerTrack(dismissIfClick: !_overlayDragPromoted);
                    return IntPtr.Zero;
                }
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case Win32Constants.WM_CAPTURECHANGED:
                if (hwnd == _hwndOverlay && _overlayDragPending && !_overlayDragPromoted)
                {
                    // 승격 경로의 ReleaseCapture 가 여기로 오므로 promoted 면 유지.
                    // 그 외 캡처 상실(Alt-Tab 등)은 pending 만 리셋 — 숨김 안 함.
                    _overlayDragPending = false;
                    return IntPtr.Zero;
                }
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case Win32Constants.WM_MOVING:
                if (hwnd == _hwndOverlay)
                {
                    RECT movingRect = Marshal.PtrToStructure<RECT>(lParam);
                    if (Overlay.HandleMoving(ref movingRect, _lastImeState,
                            _config.SnapToWindows, DefaultConfig.SnapThresholdPx, _config.SnapGapPx))
                    {
                        Marshal.StructureToPtr(movingRect, lParam, false);
                        return (IntPtr)1;
                    }
                }
                return IntPtr.Zero;

            case Win32Constants.WM_ENTERSIZEMOVE:
                if (hwnd == _hwndOverlay)
                    Overlay.BeginDrag(_config.SnapToWindows);
                return IntPtr.Zero;

            case Win32Constants.WM_EXITSIZEMOVE:
                if (hwnd == _hwndOverlay)
                    HandleOverlayDragEnd();
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // ================================================================
    // 이벤트 핸들러
    // ================================================================

    /// <summary>
    /// 현재 포그라운드 앱에 플로팅 배지를 표시한다 — <c>_indicatorVisible</c> 설정 + per-app 위치 계산
    /// + <see cref="Animation.TriggerShow"/> 를 한 곳에 모은다. IME/Focus/Activate/UserHidden 해제/
    /// Config 리프레시 등 여러 경로가 공유하던 3줄 패턴의 단일 진실원. 호출 전 <c>_lastForegroundHwnd</c>
    /// 유효성(대부분 <c>!= IntPtr.Zero</c> 가드)은 호출자가 보장한다.
    /// </summary>
    private static void ShowIndicatorAtForeground(ImeState state, AppConfig resolved, bool imeChanged)
    {
        _clickDismissed = false;
        _indicatorVisible = true;
        var (x, y) = GetAppPosition();
        Animation.TriggerShow(x, y, state, resolved, imeChanged);
    }

    private static void HandleImeStateChanged(ImeState newState)
    {
        _lastImeState = newState;
        Logger.Debug($"IME state: {newState}");

        // 트레이 아이콘은 항상 IME 상태 반영 — 트레이는 글로벌 영역 (per-app 비대상)
        if (_config.TrayEnabled)
            Tray.UpdateState(newState, _config);

        // 커서 헤일로는 IME 변경 시 색상 갱신 (가시 중이면 즉시 재렌더). enabled=false 면 무동작.
        if (_config.CursorIndicatorEnabled)
            CursorOverlay.SetImeState(newState);

        if (_config.UserHidden) return;
        if (_lastForegroundHwnd == IntPtr.Zero) return;

        // PR-13: DisplayMode / EventTriggers / 렌더 인자 모두 per-app resolved 사용.
        // 좌클릭 일시 숨김(_clickDismissed) 중이면 EventTriggers 와 무관하게 한·영 변경으로 재표시.
        AppConfig resolved = ResolveCurrent();
        if (_clickDismissed
            || resolved.DisplayMode == DisplayMode.Always
            || resolved.EventTriggers.OnImeChange)
            ShowIndicatorAtForeground(newState, resolved, imeChanged: true);
    }

    private static void HandleFocusChanged(IntPtr newHwndFocus)
    {
        if (_config.UserHidden) return;
        if (_lastForegroundHwnd == IntPtr.Zero) return;

        AppConfig resolved = ResolveCurrent();
        // 좌클릭 일시 숨김 중이면 EventTriggers 와 무관하게 포커스 변경으로 재표시.
        if (_clickDismissed
            || resolved.DisplayMode == DisplayMode.Always
            || resolved.EventTriggers.OnFocusChange)
            ShowIndicatorAtForeground(_lastImeState, resolved, imeChanged: false);
    }

    private static void HandlePositionUpdated(IntPtr hwndForeground)
    {
        bool foregroundChanged = hwndForeground != _lastForegroundHwnd;
        // wasHidden: 같은 앱으로 복귀했으나 직전에 인디가 숨겨져 있던 경우
        // (데스크톱 클릭 → 같은 앱 복귀 시나리오 — 감지 스레드는 변경을 인지하지만
        //  메인 스레드 _lastForegroundHwnd는 같은 값이므로 추가 트리거 필요).
        bool wasHidden = !_indicatorVisible;
        _lastForegroundHwnd = hwndForeground;

        if (foregroundChanged)
            _currentProcessName = WindowProcessInfo.GetProcessName(hwndForeground);

        if (_config.UserHidden) return;

        // 좌클릭 일시 숨김은 포커스/IME 경로에서만 해제 — POSITION_UPDATED 의 wasHidden
        // 재표시로 즉시 되살아나지 않게 한다 (창 이동 종료 등).
        if (_clickDismissed) return;

        // 시스템 입력 프로세스(시작 메뉴 ↔ 검색 창)는 하나의 HWND를 모드별로 재사용하면서
        // 시각적 rect만 바꾼다. 감지 스레드가 rect 변화 기반으로 이 메시지를 다시 보낸 경우
        // foregroundChanged가 false여도 위치를 재계산해 실제 시각 rect에 맞춰야 한다.
        bool sysInput = DefaultConfig.IsSystemInputProcess(_currentProcessName);

        if (foregroundChanged || wasHidden || sysInput)
        {
            _indicatorVisible = true;
            var (x, y) = GetAppPosition();
            // hwnd/class 를 함께 남긴다 — 한 프로세스가 top-level 창을 여러 개 쓰는 앱(파일 관리자의
            // 내장 뷰어 등)에서 프로세스명만으로는 어느 창에 인디가 붙었는지 구분할 수 없어 진단이 막힌다.
            // GetClassName 은 P/Invoke 라 레벨 가드로 감싼다 (Logger.IsEnabled 계약 참조).
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.Debug($"PositionUpdated: process={_currentProcessName}, hwnd=0x{hwndForeground.ToInt64():X}, " +
                             $"class={WindowProcessInfo.GetClassName(hwndForeground)}, pos=({x},{y}), saved={_config.IndicatorPositions.Count}");
            // PR-13: per-app resolved (theme/색/투명도/폰트/라벨 등 시각 override 반영)
            Animation.TriggerShow(x, y, _lastImeState, ResolveCurrent(), imeChanged: false);
        }
        // 같은 앱 내 윈도우 이동 — 플로팅 배지는 위치 고정이므로 무시
    }

    /// <summary>
    /// 현재 포그라운드 앱에 대한 per-app resolved AppConfig 반환 (PR-13).
    /// 프로필이 없거나 매치 실패 시 글로벌 <c>_config</c> 그대로.
    /// <para>
    /// <see cref="Settings.ResolveForApp"/> 가 <c>enabled:false</c> 프로필에 대해 null 을
    /// 반환할 수 있다. 감지 스레드 <see cref="DetectionService"/> 필터가 보통 먼저 숨기지만,
    /// UserHidden 해제·Activate 등 강제 Show 경로는 <see cref="TryShowIndicatorIfForegroundAllowed"/>
    /// 가 라이브 재평가로 차단한다. 여기 null 폴백은 짧은 race 방어용이다.
    /// </para>
    /// <para>
    /// 호출 비용: <see cref="Settings.ResolveForApp"/> 의 LRU 캐시가 같은 프로세스명 키에서
    /// 즉시 hit 한다. 첫 호출만 JSON merge + Validate + Theme 파이프라인 (수 ms) 통과.
    /// 캐시 무효화는 <see cref="HandleConfigChanged"/> / <see cref="HandleSettingChange"/> 에서.
    /// </para>
    /// </summary>
    private static AppConfig ResolveCurrent()
    {
        if (_lastForegroundHwnd == IntPtr.Zero) return _config;
        return Settings.ResolveForApp(_config, _lastForegroundHwnd) ?? _config;
    }

    /// <summary>
    /// 라이브 포그라운드에 대해 SystemFilter / <c>enabled:false</c> / Pointer suppress(PR-32) 를
    /// 재평가한 뒤 통과할 때만 인디를 표시한다 (PR-26). UserHidden 해제·두 번째 인스턴스 Activate 등
    /// 강제 Show 경로용. stale <c>_lastForegroundHwnd</c> 를 쓰지 않는다.
    /// 히스테리시스 없음(즉시 판정) — 탐색기 flip-flop 으로 한 번 스킵돼도 다음 non-filter
    /// 틱의 <c>WM_POSITION_UPDATED</c> 로 자기치유.
    /// </summary>
    private static void TryShowIndicatorIfForegroundAllowed(ImeState state, bool imeChanged)
    {
        IntPtr hwndFg = User32.GetForegroundWindow();
        if (hwndFg == IntPtr.Zero
            || hwndFg == _hwndMain
            || hwndFg == _hwndOverlay
            || (_hwndCursorOverlay != IntPtr.Zero && hwndFg == _hwndCursorOverlay))
        {
            Logger.Info("Forced show skipped: no usable foreground window");
            return;
        }

        uint threadId = User32.GetWindowThreadProcessId(hwndFg, out _);
        IntPtr hwndFocus = DetectionService.ResolveFocusWindow(threadId, hwndFg);
        AppConfig? resolved = Settings.ResolveForApp(_config, hwndFg);
        if (resolved is null || SystemFilter.ShouldHide(hwndFg, hwndFocus, resolved))
        {
            Logger.Info(
                $"Forced show skipped: foreground filtered (hwnd=0x{hwndFg.ToInt64():X}, class={WindowProcessInfo.GetClassName(hwndFg)})");
            return;
        }

        // PR-32: 메뉴·셸 표면 위에서는 FG가 통과해도 Show 금지 (커서 WFP 축과 대칭).
        if (OverlaySuppressProbe.IsPointerOverSuppressSurface(_config, includeSystemInputProcesses: false))
        {
            Logger.Info("Forced show skipped: pointer over suppress surface");
            return;
        }

        _lastForegroundHwnd = hwndFg;
        _currentProcessName = WindowProcessInfo.GetProcessName(hwndFg);
        ShowIndicatorAtForeground(state, resolved, imeChanged);
    }

    /// <summary>
    /// 현재 앱의 저장 위치 반환. 없으면 기본 위치.
    /// 시스템 입력 프로세스는 항상 기본 위치 — 저장 위치를 무시 (z-band 가시성 보장).
    /// 저장 위치는 모니터 제거 / 해상도 변경으로 화면 밖이 될 수 있으므로 가시 영역으로 클램프한다.
    /// </summary>
    private static (int x, int y) GetAppPosition()
    {
        // 시스템 입력 프로세스: 모드 무관하게 기존 방식
        if (DefaultConfig.IsSystemInputProcess(_currentProcessName))
            return Overlay.GetDefaultPosition(_lastForegroundHwnd, _currentProcessName);

        if (_config.PositionMode == PositionMode.Window)
            return GetAppPositionWindow();

        return GetAppPositionFixed();
    }

    /// <summary>고정 모드 위치 조회 (기존 로직).</summary>
    private static (int x, int y) GetAppPositionFixed()
    {
        // 1. 런타임 hwnd별 위치 (세션 내 창별 구분)
        if (_lastForegroundHwnd != IntPtr.Zero
            && _hwndPositions.TryGetValue(_lastForegroundHwnd, out var hwndPos))
        {
            return ClampToVisibleArea(hwndPos.x, hwndPos.y);
        }
        // 2. config 프로세스명별 위치 (영구 저장)
        if (_currentProcessName.Length > 0
            && _config.IndicatorPositions.TryGetValue(_currentProcessName, out int[]? pos)
            && pos.Length >= 2)
        {
            return ClampToVisibleArea(pos[0], pos[1]);
        }
        // 3. 기본 위치 (포그라운드 창 모니터 기준, config 기본 위치 적용)
        return Overlay.GetDefaultPosition(_lastForegroundHwnd, _currentProcessName);
    }

    /// <summary>창 기준 모드 위치 조회 — 창 DWM 프레임 기준 상대 오프셋 → 절대좌표 변환.</summary>
    private static (int x, int y) GetAppPositionWindow()
    {
        // 1. config 프로세스명별 상대 위치
        if (_currentProcessName.Length > 0
            && _config.IndicatorPositionsRelative.TryGetValue(_currentProcessName, out int[]? rel)
            && rel.Length >= 3
            && Enum.IsDefined((Corner)rel[0])
            && _lastForegroundHwnd != IntPtr.Zero
            && Dwmapi.TryGetVisibleFrame(_lastForegroundHwnd, out RECT frame))
        {
            var relConfig = new RelativePositionConfig
            {
                Corner = (Corner)rel[0],
                DeltaX = rel[1],
                DeltaY = rel[2],
            };
            // Delta 는 논리 px — 타겟 창의 모니터 DPI 스케일로 승산해 물리 px 변환 후 적용.
            double dpiScale = DpiHelper.GetScale(
                User32.MonitorFromWindow(_lastForegroundHwnd, Win32Constants.MONITOR_DEFAULTTONEAREST));
            var (x, y) = Overlay.ResolveRelativePosition(frame, relConfig, dpiScale);
            return ClampToVisibleArea(x, y);
        }
        // 2. 기본 상대 위치 (창 프레임 기준 — 창이 화면 가장자리면 work area 밖일 수 있어 클램프)
        var def = Overlay.GetDefaultRelativePosition(
            _lastForegroundHwnd, _currentProcessName,
            _config.DefaultIndicatorPositionRelative);
        return ClampToVisibleArea(def.x, def.y);
    }

    /// <summary>
    /// 표시용 절대좌표를 현재 살아있는 모니터의 작업 영역 안으로 클램프.
    /// 저장 좌표 읽기 · Window 기본 resolve · 드래그 종료 Show 등에서 사용.
    /// 모니터 제거 / 해상도 변경 / DPI 변경 후 화면 밖이 될 수 있는 문제를 방어.
    /// Fixed 저장 값 자체는 덮어쓰지 않아서 원 모니터 복귀 시 원 위치가 복원된다.
    /// </summary>
    private static (int x, int y) ClampToVisibleArea(int x, int y)
    {
        var (w, h) = Overlay.GetBaseSize();
        if (w <= 0 || h <= 0) return (x, y);  // 엔진 아직 초기화 전

        // 배지 중심점 기준 가장 가까운 살아있는 모니터로 라우팅 (DEFAULTTONEAREST).
        // 저장 좌표가 제거된 모니터에 있었다면 잔존 모니터 중 가장 가까운 쪽으로 재매핑된다.
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x + w / 2, y + h / 2);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);

        // 배지 bbox 가 작업 영역 폭/높이를 초과하면 Left/Top 으로 고정 (Math.Clamp 역방향 방어).
        int maxX = Math.Max(workArea.Left, workArea.Right - w);
        int maxY = Math.Max(workArea.Top, workArea.Bottom - h);
        int clampedX = Math.Clamp(x, workArea.Left, maxX);
        int clampedY = Math.Clamp(y, workArea.Top, maxY);

        if (clampedX != x || clampedY != y)
            Logger.Debug($"Position clamped: ({x},{y}) -> ({clampedX},{clampedY})");

        return (clampedX, clampedY);
    }

    private static void HandleConfigChanged()
    {
        AppConfig prev = _config;
        _config = Settings.Load();
        Logger.SetLevel(_config.LogLevel);
        // Logger.Initialize 는 drain 스레드를 종료(Join 최대 3s)·재시작하는 무거운 작업이라, 로그 관련
        // 설정이 실제로 바뀐 경우에만 재초기화한다 — 무변경 리로드가 메인 스레드를 블록하지 않도록.
        if (prev.LogToFile != _config.LogToFile
            || prev.LogFilePath != _config.LogFilePath
            || prev.LogMaxSizeMb != _config.LogMaxSizeMb)
        {
            string resolvedLogPath = PortablePath.SanitizeLogPath(_config.LogFilePath, out string? logPathReject);
            Logger.Initialize(_config.LogToFile, resolvedLogPath, _config.LogMaxSizeMb);
            if (logPathReject is not null)
                Logger.Warning($"{logPathReject}; using '{resolvedLogPath}'");
        }
        I18n.Load(_config.Language);
        Settings.ClearProfileCache();
        ImeStatus.UpdateDetectionMethod(_config.DetectionMethod);
        // 글로벌 기준으로 엔진 캐시 재빌드 — 다음 per-app TriggerShow 가 style 차이 시 추가 무효화.
        Overlay.HandleConfigChanged(_config);

        // 커서 헤일로 lifecycle — config.json 리로드 경로. HandleMenuCommand 람다도 동일 헬퍼 호출.
        ApplyCursorConfigChange();

        // PR-26: config 핫리로드로 user_hidden false→true 시 즉시 숨김 (이전엔 가시 인디가 동결).
        if (!prev.UserHidden && _config.UserHidden)
        {
            if (_indicatorVisible)
                HideOverlay("config UserHidden");
        }
        else if (!_config.UserHidden)
            RefreshVisibleIndicator();
        if (_config.TrayEnabled)
            Tray.UpdateState(_lastImeState, _config);
        Logger.Info("Config reloaded");
    }

    /// <summary>
    /// UpdateChecker 워커 스레드의 콜백. volatile 필드에 페이로드를 게시한 뒤
    /// 메인 메시지 큐로 WM_APP_UPDATE_FOUND 를 PostMessage 한다 — 본 람다는
    /// 워커 스레드에서 실행되므로 GUI 작업을 직접 하면 안 됨.
    /// </summary>
    private static void OnUpdateCheckResult(IntPtr hwndMain, UpdateInfo info)
    {
        _pendingUpdate = info;
        if (hwndMain != IntPtr.Zero)
            User32.PostMessageW(hwndMain, AppMessages.WM_APP_UPDATE_FOUND, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 메인 스레드: 새 버전 알림을 트레이에 등록. Tray 가 메뉴 빌드 시점에 페이로드를 읽어
    /// 최상단 헤더 라벨을 "KoEnVue v{cur} → {newTag} — 다운로드" 로 합성한다 (평소 라벨
    /// "KoEnVue v{cur} — GitHub" 에서 같은 항목의 텍스트만 전환 — 메뉴 항목 추가 없음).
    /// </summary>
    private static void HandleUpdateFound()
    {
        var info = _pendingUpdate;
        if (info is null) return;
        Tray.OnUpdateFound(info);
    }

    /// <summary>
    /// 중복 실행된 두 번째 인스턴스의 WM_APP_ACTIVATE 수신 핸들러.
    /// 현재 포그라운드 앱 기준으로 플로팅 배지를 즉시 표시해 "이미 실행 중" 이라는 시각 피드백을 준다.
    /// DisplayMode / EventTriggers 설정과 무관하게 강제 표시 — 사용자의 명시적 재실행 행위에 대한 응답.
    /// </summary>
    private static void HandleActivateRequest()
    {
        Logger.Info("Activation request from second instance received");
        if (_config.UserHidden) return;
        // PR-26: 라이브 FG SystemFilter 재평가 — 바탕화면에서 바로가기 재실행 시 필터 대상 위 표시 방지
        TryShowIndicatorIfForegroundAllowed(_lastImeState, imeChanged: false);
    }

    private static void HideOverlay(string source = "?")
    {
        // 숨김 경로 추적 — source 로 호출자(시스템 필터 / 트레이 토글 / 세션 잠금)를 식별해
        // "플로팅 배지가 안 보인다" 류 문제의 원인 경로를 로그만으로 좁힌다.
        Logger.Info($"HideOverlay called: source={source}");
        // PR-26 (c): 숨김 시 시스템 입력 패널 프레임 캐시 무효화.
        // SearchHost→StartMenu 전환은 Hide 없이 가시 유지하므로 보정 캐시는 그 경로에서 보존됨.
        Overlay.ClearLastValidSystemInputFrame();
        // forceHidden: Always 모드에서도 Idle이 아닌 완전 숨김으로 전환.
        // 시스템 필터(바탕화면/작업 표시줄), 트레이 토글 OFF 모두
        // "실제로 사라져야 하는" 의도이므로 Always 모드의 dim-idle 유지를 우회.
        Animation.TriggerHide(_config, forceHidden: true);
        _indicatorVisible = false;
    }

    private static void HandleTrayCallback(IntPtr lParam)
    {
        uint mouseEvent = (uint)(lParam.ToInt64() & Win32Constants.LOWORD_MASK);
        switch (mouseEvent)
        {
            case Win32Constants.WM_CONTEXTMENU:
                Tray.ShowMenu(_hwndMain, _config);
                break;
            case Win32Constants.WM_LBUTTONUP:
                switch (_config.TrayClickAction)
                {
                    case TrayClickAction.Toggle:
                        HandleTrayToggle();
                        break;
                    case TrayClickAction.Settings:
                        Tray.OpenConfigFile();
                        break;
                }
                break;
        }
    }

    /// <summary>
    /// 트레이 좌클릭 토글: UserHidden 상태를 반전하고 인디를 즉시 숨김/표시.
    /// UserHidden=true 로 전환: 배지 숨김 + 트레이 아이콘에 취소선 오버레이.
    /// UserHidden=false 로 전환: 현재 포그라운드 앱에 배지 즉시 재표시 + 취소선 제거.
    /// config.json 에 즉시 저장 — 재기동/포그라운드 전환에도 상태 유지.
    /// </summary>
    private static void HandleTrayToggle()
    {
        bool wasHidden = _config.UserHidden;
        _config = _config with { UserHidden = !wasHidden };
        Settings.Save(_config);
        Logger.Info($"Tray toggle: UserHidden={_config.UserHidden}");

        // 트레이 아이콘 재생성 — 취소선 표시/제거 반영
        if (_config.TrayEnabled)
            Tray.UpdateState(_lastImeState, _config);

        ApplyUserHiddenTransition(wasHidden, _config.UserHidden);
    }

    /// <summary>
    /// UserHidden 전환을 오버레이에 반영한다. HandleTrayToggle(좌클릭) 과
    /// HandleMenuCommand 의 updateConfig 람다(메뉴 "플로팅 배지 숨김" 토글 + 향후
    /// SettingsDialog 등) 양 경로에서 공유. 호출 전 <c>_config.UserHidden</c> 은 이미 새 값으로
    /// 갱신돼 있어야 한다.
    /// </summary>
    private static void ApplyUserHiddenTransition(bool wasHidden, bool isHidden)
    {
        if (wasHidden == isHidden) return;

        if (isHidden)
        {
            // 숨김 전환: 현재 가시 상태라면 즉시 숨김
            if (_indicatorVisible)
                HideOverlay("UserHidden toggle");
        }
        else
        {
            // 표시 전환: 라이브 포그라운드 SystemFilter 재평가 후만 표시 (PR-26).
            // stale _lastForegroundHwnd / 닫힌 검색 패널 좌표에 그리는 경로를 차단.
            TryShowIndicatorIfForegroundAllowed(_lastImeState, imeChanged: false);
        }
    }

    private static void HandleMenuCommand(int commandId)
    {
        Tray.HandleMenuCommand(commandId, _config, _hwndMain, _lastForegroundHwnd,
            updateConfig: newConfig =>
            {
                bool wasHidden = _config.UserHidden;
                AppLanguage oldLanguage = _config.Language;
                _config = ThemePresets.Apply(newConfig);
                // 자체 Settings.Save 는 mtime self-bump 로 WM_CONFIG_CHANGED 를 차단하므로
                // HandleConfigChanged 를 통한 I18n.Load 갱신 경로가 동작 안 한다. 사용자 가시
                // 전환을 위해 람다 안에서 직접 재로드. Tray.UpdateState 가 뒤따라 fresh string.
                if (oldLanguage != _config.Language)
                    I18n.Load(_config.Language);
                ImeStatus.UpdateDetectionMethod(_config.DetectionMethod);
                Overlay.HandleConfigChanged(_config);
                // 커서 헤일로 lifecycle — mtime self-bump 로 HandleConfigChanged 우회되므로 직접 호출.
                ApplyCursorConfigChange();

                if (wasHidden != _config.UserHidden)
                {
                    // UserHidden 토글 — 좌클릭 HandleTrayToggle 과 동일한 표시/숨김 전환 적용
                    ApplyUserHiddenTransition(wasHidden, _config.UserHidden);
                }
                // 인디가 가시 상태라면 애니메이터 config 갱신 + 새 alpha/크기/색상이 즉시 반영되도록
                // TriggerShow로 전체 갱신. TriggerShow는 UpdateConfig + Overlay.Show를 포함한다.
                // PR-13: per-app resolved 로 갱신해 메뉴 변경 시점부터 프로필 시각 override 도 즉시 반영.
                else if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
                    ShowIndicatorAtForeground(_lastImeState, ResolveCurrent(), imeChanged: false);
                else if (_indicatorVisible)
                {
                    Overlay.UpdateColor(_lastImeState, ResolveCurrent());
                }
                if (_config.TrayEnabled)
                    Tray.UpdateState(_lastImeState, _config);
                Settings.Save(_config);
                // 트레이/메뉴 토글로 글로벌 옵션이 바뀌면 per-app 머지 결과가 stale.
                // HandleConfigChanged/HandleSettingChange 두 경로는 이미 ClearProfileCache 를
                // 호출하지만 이 람다는 mtime self-bump 가 없어 별도 무효화가 필요하다.
                Settings.ClearProfileCache();
            });
    }

    // ================================================================
    // 2-스레드 모델
    // ================================================================

    // --- 감지 스레드 (본문은 DetectionService.RunLoop 위임) ---

    private static void StartDetectionThread()
    {
        var host = new DetectionHost
        {
            GetConfig = static () => _config,
            GetHwndMain = static () => _hwndMain,
            GetHwndOverlay = static () => _hwndOverlay,
            GetHwndCursorOverlay = static () => _hwndCursorOverlay,
            IsIndicatorVisible = static () => _indicatorVisible,
            IsSessionLocked = static () => _sessionLocked,
            IsStopping = static () => _stopping,
        };
        _detectionThread = new Thread(() => DetectionService.RunLoop(host))
        {
            IsBackground = true,
            Name = "KoEnVue-Detection",
        };
        _detectionThread.Start();
    }

}
