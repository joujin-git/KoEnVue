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
    /// <summary>
    /// 세션 내 창별 배지 위치 (Fixed 모드). 값에 <b>프로세스명을 함께</b> 담는다 —
    /// 커널은 파괴된 창의 HWND 값을 재발급하므로, 원시 HWND 만 키로 쓰면 <b>다른 앱의 새 창이 죽은
    /// 창의 좌표를 물려받는다.</b> 이 경로가 <c>indicator_positions</c>(프로세스명 영구 저장)보다
    /// 우선순위가 높아, 그 오식별이 사용자가 저장해 둔 위치를 덮어버렸다 (AUDIT-2026-07-30 §L).
    /// 조회 시 프로세스명이 일치하지 않으면 재활용으로 보고 그 항목을 버린다.
    /// <para>
    /// 제거 경로가 전혀 없어 상주 앱에서 단조 증가하던 문제도 함께 닫는다 —
    /// <see cref="HwndPositionMaxEntries"/> 초과 시 죽은 창부터 정리한다.
    /// </para>
    /// </summary>
    private static readonly Dictionary<IntPtr, (int x, int y, string process)> _hwndPositions = [];

    /// <summary>
    /// <see cref="_hwndPositions"/> 상한. 초과 시 <see cref="PruneHwndPositions"/> 가 죽은 창을 정리한다.
    /// 한 세션에서 사용자가 배지를 개별 배치하는 창이 이보다 많기는 어렵다.
    /// </summary>
    private const int HwndPositionMaxEntries = 64;

    // CAPS LOCK 토글 캐시 (메인 스레드 전용 — TIMER_ID_CAPS 폴러가 200ms마다 GetKeyState 비교)
    private static bool _lastCapsLockState;

    // WM_IME_STATE_CHANGED 를 한 번이라도 받았는지 (메인 스레드 전용). _lastImeState 의 초기값이
    // 실제 상태와 우연히 같을 수 있어, 첫 메시지를 중복으로 오인해 버리지 않도록 하는 래치 (§N-55).
    private static bool _imeStateReceived;

    // config.json 핫리로드가 파싱 실패 중인지 (메인 스레드 전용 — HandleConfigChanged).
    // 안내 박스를 연속 실패당 1회로 제한하는 래치. 정상 로드 시 해제되므로, 사용자가 파일을 고친 뒤
    // 다시 깨뜨리면 새로 한 번 더 안내한다.
    private static bool _configReloadFailed;

    // HandleConfigChanged 재진입 가드 (메인 스레드 전용 — 락/volatile 불필요).
    // 리로드 실패 안내는 MessageBoxW 라 Win32 자체 모달 루프가 _hwndMain 앞으로 post 된 메시지를
    // 그대로 디스패치하고, 감지 스레드는 모달 여부와 무관하게 5초 폴링을 계속한다 — 사용자가 그
    // 사이 파일을 고쳐 저장하면 안내 박스 **안에서** 리로드가 재진입한다 (확정 #34).
    // Pending 은 그 재진입을 버리지 않기 위한 표식이다: 바깥 프레임이 끝날 때 한 번 더 처리한다.
    private static bool _configChangeInProgress;
    private static bool _configChangePending;

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

        // 9-1. 모달 다이얼로그가 열려 있는 동안 배지도 함께 비활성화한다. 배지는 소유자 없는 별도
        //      최상위 창이라 ModalDialogLoop 의 EnableWindow(owner, false) 대상이 아니었고, 그래서
        //      다이얼로그 뒤에서 드래그가 됐다 — 드래그 종료가 Settings.Save 까지 수행하므로 열린
        //      다이얼로그 뒤에서 설정이 갈아치워지고, 「확인」 이 그것을 되돌렸다 (확정 #39).
        //      호출 시점 조회라 파괴 후에도 안전하다. 커서 헤일로는 WS_EX_TRANSPARENT 로 입력을
        //      아예 받지 않으므로 대상이 아니다.
        ModalDialogLoop.ExtraModalWindows =
            () => _hwndOverlay != IntPtr.Zero ? [_hwndOverlay] : [];

        // 9a. 렌더링 + 애니메이션 초기화
        Logger.Debug("Initializing overlay rendering");
        Overlay.Initialize(_hwndOverlay, _config);
        Logger.Debug("Initializing animation");
        // onHidden: 애니메이터가 fade-out 을 끝내고 스스로 숨긴 경우에도 가시 플래그를 내린다.
        // 이 훅이 없으면 HideOverlay 를 거치지 않는 그 경로에서만 _indicatorVisible 이 true 로 남아
        // 메인·감지 로직이 영구히 "보이는 중" 으로 오판한다 (AUDIT-2026-07-30 §N-34).
        Animation.Initialize(_hwndMain, _hwndOverlay, _config,
            onHidden: static () => _indicatorVisible = false);

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
        // WinEvent 콜백이 감지 루프와 **같은 기준**(per-app resolved)으로 판정하도록 배선한다 (§N-50).
        // 프로필이 없거나 매칭 실패면 ResolveForApp 이 global 을 그대로 돌려주므로 종전 동작과 같다.
        ImeStatus.SetPerAppDetectionMethodResolver(
            static hwnd => (Settings.ResolveForApp(_config, hwnd) ?? _config).DetectionMethod);

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
        //     등록 시점의 스레드를 기록해 둔다 — OnProcessExit 단계 5 의 DestroyWindow 는 창을 만든
        //     스레드에서만 성공하는데, 같은 함수 단계 7 의 주석은 "ProcessExit 는 finalizer 스레드에서
        //     돈다" 고 단언한다. 둘 다 참일 수 없어 한쪽이 결함이다 (bug-hunt 2026-08-02 확정 #45).
        //     실측 없이 어느 쪽인지 단정할 수 없으므로 계측만 넣고 로그로 확정한다.
        _mainThreadId = Environment.CurrentManagedThreadId;
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
                // **파괴 직후 해당 핸들 필드를 즉시 비운다.** AUDIT-2026-07-30 §N-42 가 세운 이
                // invariant 의 구현은 OnProcessExit 한 곳에만 있었는데, 창이 그보다 **먼저** 죽는
                // 경로가 있다 — WndProcCore 에 WM_CLOSE case 가 없어 DefWindowProcW 가 곧바로
                // DestroyWindow 를 수행하고, 뒤따르는 여기서는 PostQuitMessage 만 했다. 그 사이
                // (메시지 큐가 WM_QUIT 까지 배수되고 MainImpl 이 반환할 때까지, 감지 루프의 sleep
                // 한 틱 이상) 감지 스레드는 `!= IntPtr.Zero` 가드를 통과해 죽은 HWND 에 계속
                // PostMessageW 를 보낸다 — 커널이 그 값을 재발급하면 무관한 창에 WM_APP 범위
                // 메시지가 배달된다(§L 과 같은 재활용 문제).
                //
                // 예외 경로가 아니다 — 트레이 「관리자 권한」 토글이 PostMessageW(hwndMain,
                // WM_CLOSE) 로 이 경로를 **정상 동작으로** 탄다 (Tray.HandleMenuCommand).
                // 세 창이 같은 WndProc 을 공유하므로(Program.Bootstrap.RegisterWindowClasses)
                // 어느 창이 죽었는지 판별해 그 필드만 내린다
                // (bug-hunt 2026-08-02 확정 #10·#40·#43).
                if (hwnd == _hwndMain)
                {
                    _hwndMain = IntPtr.Zero;
                    User32.PostQuitMessage(0);
                }
                else if (hwnd == _hwndOverlay)
                    _hwndOverlay = IntPtr.Zero;
                else if (hwnd == _hwndCursorOverlay)
                    _hwndCursorOverlay = IntPtr.Zero;
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
    ///
    /// <para>
    /// <c>_indicatorVisible</c> 은 <b>요청이 아니라 결과</b>로 세운다. 선-대입하면 <c>TriggerShow</c> 의
    /// NonKorean + Hide 가드가 표시 없이 숨김으로 빠질 때 플래그만 true 로 박제된다 — 그 경로는
    /// 애니메이터가 이미 <c>Hidden</c> phase 면 <c>onHidden</c> 훅조차 발화하지 않아 되돌릴 기회가 없다.
    /// 이 플래그는 감지 스레드가 <c>DetectionHost.IsIndicatorVisible</c> 로 읽는 <b>유일한 가시성 계약</b>
    /// 이라, 거짓 true 는 매 틱 불필요한 <c>WM_HIDE_INDICATOR</c> 를 유발하고 <c>HandlePositionUpdated</c>
    /// 의 재표시 판정(<c>wasHidden</c>)을 무력화한다 (bug-hunt 2026-08-02 확정 #7·#17·#25·#37).
    /// </para>
    /// </summary>
    private static void ShowIndicatorAtForeground(ImeState state, AppConfig resolved, bool imeChanged)
    {
        _clickDismissed = false;
        var (x, y) = GetAppPosition();
        _indicatorVisible = Animation.TriggerShow(x, y, state, resolved, imeChanged);
    }

    private static void HandleImeStateChanged(ImeState newState)
    {
        // 같은 IME 전이 1회에 이 메시지가 **2회 도착**한다 — 감지 루프(DetectionService)와 WinEvent
        // 콜백(ImeStatus.OnImeChange)이 각각 post 하기 때문이다. 멱등 가드가 없으면 강조 팝
        // 애니메이션이 두 번 트리거돼 배지가 연달아 튄다 (AUDIT-2026-07-30 §N-55).
        // 첫 메시지는 _lastImeState 의 초기값(English)과 우연히 같을 수 있으므로 별도 래치로 통과시킨다.
        if (_imeStateReceived && newState == _lastImeState) return;
        _imeStateReceived = true;

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
            var (x, y) = GetAppPosition();
            // hwnd/class 를 함께 남긴다 — 한 프로세스가 top-level 창을 여러 개 쓰는 앱(파일 관리자의
            // 내장 뷰어 등)에서 프로세스명만으로는 어느 창에 인디가 붙었는지 구분할 수 없어 진단이 막힌다.
            // GetClassName 은 P/Invoke 라 레벨 가드로 감싼다 (Logger.IsEnabled 계약 참조).
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.Debug($"PositionUpdated: process={_currentProcessName}, hwnd=0x{hwndForeground.ToInt64():X}, " +
                             $"class={WindowProcessInfo.GetClassName(hwndForeground)}, pos=({x},{y}), saved={_config.IndicatorPositions.Count}");
            // PR-13: per-app resolved (theme/색/투명도/폰트/라벨 등 시각 override 반영)
            // 플래그는 **결과**로 세운다 — 선-대입 금지 이유는 ShowIndicatorAtForeground 참조 (확정 #7).
            _indicatorVisible = Animation.TriggerShow(x, y, _lastImeState, ResolveCurrent(), imeChanged: false);
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

    /// <summary>
    /// <see cref="_hwndPositions"/> 가 상한을 넘으면 이미 파괴된 창의 항목을 정리한다 (AUDIT-2026-07-30 §L).
    /// 제거 경로가 전혀 없어 상주 앱에서 단조 증가하던 것을 닫는다. 삽입 직전에만 호출되므로
    /// <c>IsWindow</c> P/Invoke 비용은 사용자가 배지를 드래그해 놓는 순간에만, 그것도 상한 근처에서만 든다.
    /// </summary>
    private static void PruneHwndPositions()
    {
        if (_hwndPositions.Count < HwndPositionMaxEntries) return;

        List<IntPtr> dead = [];
        foreach (IntPtr hwnd in _hwndPositions.Keys)
        {
            if (!User32.IsWindow(hwnd))
                dead.Add(hwnd);
        }
        foreach (IntPtr hwnd in dead)
            _hwndPositions.Remove(hwnd);

        Logger.Debug($"Pruned {dead.Count} dead hwnd position entries ({_hwndPositions.Count} remain)");

        // 살아 있는 창만으로 상한을 넘는 극단적 경우 — 가장 오래된 삽입부터 버린다.
        // Dictionary 의 열거 순서는 삽입 순서를 보장하지 않지만, 여기서 필요한 것은
        // "무한 성장하지 않는다" 뿐이라 임의 항목 제거로 충분하다.
        while (_hwndPositions.Count >= HwndPositionMaxEntries)
        {
            foreach (IntPtr hwnd in _hwndPositions.Keys)
            {
                _hwndPositions.Remove(hwnd);
                break;
            }
        }
    }

    /// <summary>
    /// config 의 <c>indicator_positions</c> 가 바뀌면 그 프로세스의 <b>세션 캐시</b>를 버린다
    /// (bug-hunt 3차 Q).
    ///
    /// <para>
    /// <see cref="_hwndPositions"/> 는 드래그로만 채워지고 정리 경로는 죽은 창·HWND 재활용·상한뿐이라
    /// <b>config 변경에 대한 무효화가 없었다.</b> 그런데 <see cref="GetAppPositionFixed"/> 는 이 캐시를
    /// <b>1순위</b>로 조회하므로, 위치 기록 정리 창에서 항목을 지우거나 파일에서 좌표를 고쳐도 그 창이
    /// 살아 있는 동안에는 옛 좌표가 계속 쓰였다(창을 닫았다 열면 새 HWND 라 저절로 풀린다).
    /// </para>
    ///
    /// <para>
    /// <b>통째로 비우지는 않는다</b> — 드래그로 만든 세션 위치는 config 에 없을 수도 있고(저장 전),
    /// 무관한 리로드가 그것을 지우면 사용자가 방금 옮긴 배지가 되돌아간다. config 쪽 항목이 실제로
    /// 달라진 프로세스의 엔트리만 버린다.
    /// </para>
    /// </summary>
    private static void InvalidateSessionPositions(AppConfig prev, AppConfig next)
    {
        Dictionary<string, int[]> before = prev.IndicatorPositions;
        Dictionary<string, int[]> after = next.IndicatorPositions;
        if (ReferenceEquals(before, after)) return;

        List<IntPtr> stale = [];
        foreach ((IntPtr hwnd, (int _, int _, string process)) in _hwndPositions)
        {
            bool hadBefore = before.TryGetValue(process, out int[]? oldPos);
            bool hasAfter = after.TryGetValue(process, out int[]? newPos);

            // 양쪽 다 없으면(= 드래그만 하고 아직 저장 안 된 위치) 그대로 둔다.
            bool unchanged = hadBefore == hasAfter
                             && (!hadBefore || oldPos.AsSpan().SequenceEqual(newPos));
            if (unchanged) continue;

            stale.Add(hwnd);
        }

        foreach (IntPtr hwnd in stale)
            _hwndPositions.Remove(hwnd);

        if (stale.Count > 0)
            Logger.Debug($"Invalidated {stale.Count} session position entries after config change");
    }

    /// <summary>고정 모드 위치 조회 (기존 로직).</summary>
    private static (int x, int y) GetAppPositionFixed()
    {
        // 1. 런타임 hwnd별 위치 (세션 내 창별 구분)
        if (_lastForegroundHwnd != IntPtr.Zero
            && _hwndPositions.TryGetValue(_lastForegroundHwnd, out var hwndPos))
        {
            // 프로세스명이 같아야 같은 창으로 인정한다 — 다르면 커널이 HWND 값을 재발급해
            // 다른 앱이 물려받은 것이고, 그 좌표를 쓰면 아래 2번(사용자가 저장한 위치)을 덮는다 (§L).
            if (hwndPos.process == _currentProcessName)
                return ClampToVisibleArea(hwndPos.x, hwndPos.y);

            _hwndPositions.Remove(_lastForegroundHwnd);
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

    /// <summary>
    /// config.json 핫리로드 진입점. <b>재진입 가드를 포함한다.</b>
    ///
    /// <para>
    /// 리로드가 실패하면 안내 <c>MessageBoxW</c> 를 띄우는데, 그것은 Win32 자체 모달 루프라
    /// <c>_hwndMain</c> 앞으로 post 된 메시지를 계속 디스패치한다. 감지 스레드는 모달 여부와 무관하게
    /// <c>CheckConfigFileChange</c> 를 돌리므로(모달 게이트보다 **앞**에 있다), 사용자가 그 사이
    /// 파일을 고쳐 저장하면 <b>안내 박스 안에서 이 함수가 재진입</b>한다. 그대로 처리하면 재진입한
    /// 리로드가 성공하면서 <see cref="_configReloadFailed"/> 래치를 풀어 "연속 실패당 1회" 설계(§G)가
    /// 깨지고 안내 박스가 무한히 쌓인다. 중첩 펌프 안에서 <c>Logger.Initialize</c> 가 drain 스레드
    /// Join(최대 3s)으로 블록하는 문제도 함께 온다 (bug-hunt 2026-08-02 확정 #34).
    /// </para>
    ///
    /// <para>
    /// 재진입을 <b>버리지는 않는다</b> — 표시만 남기고 바깥 프레임이 끝날 때 다시 처리한다.
    /// 조용히 무시하면 "파일을 고쳤는데 반영이 안 된다" 가 된다.
    /// </para>
    /// </summary>
    private static void HandleConfigChanged()
    {
        if (_configChangeInProgress)
        {
            _configChangePending = true;
            return;
        }

        _configChangeInProgress = true;
        try
        {
            do
            {
                _configChangePending = false;
                HandleConfigChangedCore();
            }
            while (_configChangePending);
        }
        finally
        {
            _configChangeInProgress = false;
        }
    }

    private static void HandleConfigChangedCore()
    {
        AppConfig prev = _config;

        // 파싱 실패를 성공과 구분한다 (AUDIT-2026-07-30 §G). 실패 시 Load 가 돌려주는 전 필드 디폴트를
        // 채택하면, 그 뒤 어떤 저장 경로(트레이 토글·드래그 종료)든 그 디폴트를 디스크에 확정해
        // **사용자 설정이 전멸**한다. config.json 은 편집 중 한순간만 파싱 불가여도 이 경로를 탄다.
        // 기존 인스턴스를 그대로 두고 물러나면, 파일이 고쳐지는 순간 다음 mtime 변화가 정상 리로드한다.
        if (!Settings.TryLoad(out AppConfig loaded))
        {
            Logger.Warning("Config reload failed; keeping previous settings in memory");
            // 연속 실패 중에는 첫 1회만 알린다 — 5초 폴링이라 매번 띄우면 편집을 방해한다.
            if (!_configReloadFailed)
            {
                _configReloadFailed = true;
                Tray.ShowConfigReloadFailed();
            }
            return;
        }
        _configReloadFailed = false;

        _config = loaded;
        ApplyConfigTransition(prev, loaded);

        Logger.Info("Config reloaded");
    }

    /// <summary>
    /// 설정 인스턴스 교체를 앱 전역에 반영한다 — 로거·I18n·프로필 캐시·감지 방식·오버레이 엔진·
    /// 커서 헤일로·배지 표시·트레이·클래스명 경고. <b><c>_config</c> 는 호출 전에
    /// <paramref name="next"/> 로 갱신돼 있어야 한다</b> — <see cref="ApplyCursorConfigChange"/> 와
    /// <see cref="ApplyTrayEnabledTransition"/> 이 필드를 직접 읽는다.
    ///
    /// <para>
    /// 진입점이 <b>둘</b>이다. (1) config.json 핫리로드(<see cref="HandleConfigChanged"/>),
    /// (2) 저장 중 3-way 병합으로 디스크의 사용자 편집이 들어온 경우(<see cref="SaveAndSync"/>).
    /// 후자를 여기로 모으지 않으면 <b>어디서도 처리되지 않는다</b> — <c>Settings.Save</c> 의 mtime
    /// self-bump 가 핫리로드를 차단하기 때문이다.
    /// </para>
    ///
    /// <para>
    /// 이전에는 저장 호출자마다 자기 전이만 적용했고, 병합으로 들어온 값은 <c>_config</c> 에만
    /// 실렸다 — 커서 헤일로·트레이·로거는 <b>병합 전 값으로 이미 실행을 끝낸 뒤</b>라 화면과
    /// 설정이 영구히 어긋났다(자기치유 경로도 self-bump 로 막혀 있다). 호출자 규율에 의존하는
    /// 구조였던 것이 원인이라, 개별 호출자를 고치는 대신 진입점을 하나로 모은다
    /// (bug-hunt 2026-08-02 확정 #28·#31·#47).
    /// </para>
    ///
    /// <para>
    /// 전이 판정은 전부 <paramref name="prev"/>/<paramref name="next"/> 비교라 <b>두 번 호출해도
    /// 안전하다</b> — 저장 호출자가 자기 변경을 먼저 적용한 뒤 병합이 겹쳐 들어오는 경우, 두 번째
    /// 호출은 실제로 달라진 것만 처리한다.
    /// </para>
    /// </summary>
    private static void ApplyConfigTransition(AppConfig prev, AppConfig next)
    {
        Logger.SetLevel(next.LogLevel);
        // Logger.Initialize 는 drain 스레드를 종료(Join 최대 3s)·재시작하는 무거운 작업이라, 로그 관련
        // 설정이 실제로 바뀐 경우에만 재초기화한다 — 무변경 리로드가 메인 스레드를 블록하지 않도록.
        if (prev.LogToFile != next.LogToFile
            || prev.LogFilePath != next.LogFilePath
            || prev.LogMaxSizeMb != next.LogMaxSizeMb)
        {
            string resolvedLogPath = PortablePath.SanitizeLogPath(next.LogFilePath, out string? logPathReject);
            Logger.Initialize(next.LogToFile, resolvedLogPath, next.LogMaxSizeMb);
            if (logPathReject is not null)
                Logger.Warning($"{logPathReject}; using '{resolvedLogPath}'");
        }
        I18n.Load(next.Language);
        Settings.ClearProfileCache();
        ImeStatus.UpdateDetectionMethod(next.DetectionMethod);
        // 글로벌 기준으로 엔진 캐시 재빌드 — 다음 per-app TriggerShow 가 style 차이 시 추가 무효화.
        Overlay.HandleConfigChanged(next);

        // 커서 헤일로 lifecycle.
        ApplyCursorConfigChange();

        // user_hidden 전이는 트레이 좌클릭·메뉴와 **같은 헬퍼**로 처리한다 (P4).
        // 종전에는 리로드만 RefreshVisibleIndicator 로 떨어졌는데, 그 헬퍼에는 `_indicatorVisible`
        // 가드가 걸려 있어 **숨김 상태에서 항상 no-op** 이다 — true→false 전이의 출발점이 바로 그
        // 상태이므로, 파일에서 user_hidden 을 false 로 되돌려도 배지가 나타나지 않았다. 감지 스레드도
        // 구제하지 못한다: WM_POSITION_UPDATED 는 foregroundChanged 일 때만 post 되는데, 편집기에
        // 포커스를 둔 채 저장하면 포그라운드가 바뀌지 않는다. 세 경로(좌클릭·메뉴·리로드) 중 리로드만
        // 비대칭이었다 (bug-hunt 2026-08-02 확정 #48).
        ApplyUserHiddenTransition(prev.UserHidden, next.UserHidden);

        // 전이가 없었고 계속 보이는 상태면 스타일만 갱신한다 (색·크기·폰트 변경 반영).
        if (prev.UserHidden == next.UserHidden && !next.UserHidden)
            RefreshVisibleIndicator();

        ApplyTrayEnabledTransition(prev.TrayEnabled, next.TrayEnabled);

        InvalidateSessionPositions(prev, next);

        // overlay_class_name 은 부팅 시 1회 등록이라 런타임 변경을 반영할 수 없다 (AUDIT-2026-07-30 §H).
        // 조용히 무시하면 "고쳤는데 왜 그대로냐" 가 되고, 창 생성이 새 값을 쓰면 미등록 클래스로 실패한다.
        if (next.Advanced.OverlayClassName != _registeredOverlayClassName)
        {
            Logger.Warning(
                $"overlay_class_name changed to '{next.Advanced.OverlayClassName}' but window classes are "
                + $"registered once at startup; still using '{_registeredOverlayClassName}'. Restart to apply.");
        }
    }

    /// <summary>
    /// <c>tray_enabled</c> 의 런타임 전이를 반영한다 (AUDIT-2026-07-30 §K).
    ///
    /// <para>
    /// 이전에는 리로드 경로가 <c>if (_config.TrayEnabled) Tray.UpdateState(...)</c> 만 해서
    /// <b>전이 자체를 아무도 처리하지 않았다</b> — true→false 에서는 셸 등록과 HICON 이 그대로 남아
    /// 아이콘이 계속 보였고(설정을 껐는데 사라지지 않음), false→true 에서는 <c>Initialize</c> 를 부른 적이
    /// 없으니 재시작 전까지 영영 생기지 않았다.
    /// </para>
    /// </summary>
    private static void ApplyTrayEnabledTransition(bool wasEnabled, bool isEnabled)
    {
        if (wasEnabled && !isEnabled)
        {
            Tray.Remove(TrayRemoveReason.Disabled);
            Logger.Info("Tray disabled by config reload");
            return;
        }

        if (!wasEnabled && isEnabled)
        {
            Tray.Initialize(_hwndMain, _lastImeState, _config);
            Logger.Info("Tray enabled by config reload");
            return;
        }

        if (isEnabled)
            Tray.UpdateState(_lastImeState, _config);
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
                // 좌클릭도 모달 중에는 막는다. §A 는 ShowMenu 에만 가드를 넣었는데 이 분기는 그 경로를
                // 거치지 않아 열려 있었다 — 상세 설정이 떠 있는 동안 좌클릭이 UserHidden /
                // CursorIndicatorEnabled 를 바꾸고 헤일로 창까지 재생성하는데, 그 둘은 다이얼로그
                // 노출 필드라 「확인」 한 번에 컨트롤 값으로 되돌아간다 (릴리즈 리뷰 2026-08-01 확정 #16).
                if (ModalDialogLoop.RejectReentry()) break;

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
    /// 설정을 저장하고, 3-way 병합으로 <b>디스크의 사용자 편집이 새로 들어온 경우</b> 그것을
    /// 핫리로드와 <b>같은 수준으로</b> 적용한다 (<see cref="ApplyConfigTransition"/>).
    /// 저장 경로는 전부 이 헬퍼를 거쳐야 한다.
    ///
    /// <para>
    /// <c>_config</c> 를 직접 읽고 쓴다 — 이전 시그니처는 병합 결과를 <b>반환</b>했고, 호출자가
    /// 그 값을 대입하지 않으면 조용히 손실됐다(릴리즈 리뷰 2026-08-01 확정 #1 의 정체가 그
    /// 미대입이다). 반환값을 없애 그 함정 자체를 제거한다.
    /// </para>
    ///
    /// <para>
    /// 이전에는 프로필 캐시·I18n·감지 방식·오버레이 엔진 <b>4가지만</b> 다시 세웠는데, 그 사이
    /// 호출자는 커서 헤일로·트레이·표시 전이를 <b>병합 전 값으로</b> 이미 적용하고 끝낸 상태였다.
    /// 헬퍼가 "호출자가 자기 전이 판정으로 이미 수행" 을 전제했지만, 그 판정 자체가 병합 전
    /// 값 위에서 끝난 뒤라 전제가 성립하지 않았다 (확정 #28·#31·#47).
    /// </para>
    ///
    /// <para>
    /// 병합이 없었으면 <c>Save</c> 가 입력을 <b>그대로</b> 돌려주므로 참조 비교로 구분한다 — 흔한
    /// 경로에서 불필요한 재적용을 하지 않기 위함이다.
    /// </para>
    /// </summary>
    private static void SaveAndSync()
    {
        AppConfig before = _config;
        AppConfig saved = Settings.Save(before);
        _config = saved;
        if (ReferenceEquals(saved, before)) return;

        Logger.Info("Config merged with on-disk edits during save; applying merged state");
        ApplyConfigTransition(before, saved);
    }

    /// <summary>
    /// 트레이 좌클릭: 표시 상태 4단계를 순환한다 —
    /// <b>둘 다 보임 → 배지만 → 헤일로만 → 모두 숨김 → (다시) 둘 다 보임</b>.
    /// 전이 계산의 단일 진실원은 <see cref="Tray.ComputeLeftClickCycle"/> 이고, 여기서는 그
    /// 결과를 오버레이·트레이 아이콘·config.json 에 반영하는 부수효과만 담당한다.
    /// <para>
    /// 커서 헤일로 윈도우 lifecycle 은 <see cref="ApplyCursorConfigChange"/> 가 담당하며, 메뉴
    /// 경로(HandleMenuCommand 람다)와 마찬가지로 <b>직접 호출해야 한다</b> — Settings.Save 의
    /// mtime self-bump 가 WM_CONFIG_CHANGED 를 차단해 HandleConfigChanged 경로가 돌지 않기 때문.
    /// (병합이 일어난 경우는 <see cref="SaveAndSync"/> 가 별도로 처리한다.)
    /// </para>
    /// <para>
    /// 트레이 메뉴의 체크 상태는 <see cref="Tray.ShowMenu"/> 가 열릴 때마다 현재 <c>_config</c> 로
    /// 새로 구성하므로(Tray.Menu.cs 의 MF_CHECKED 분기), config 갱신만으로 즉시 반영된다.
    /// </para>
    /// config.json 에 즉시 저장 — 재기동/포그라운드 전환에도 현재 단계가 유지된다.
    /// </summary>
    private static void HandleTrayToggle()
    {
        bool wasHidden = _config.UserHidden;
        bool wasCursorEnabled = _config.CursorIndicatorEnabled;

        _config = Tray.ComputeLeftClickCycle(_config);
        // 저장 + 병합 시 전이 재적용까지 (확정 #1·#15·#28). _config 갱신은 헬퍼가 한다.
        SaveAndSync();
        Logger.Info($"Tray click cycle: {Tray.GetVisibility(_config)} " +
                    $"(UserHidden={_config.UserHidden}, CursorIndicatorEnabled={_config.CursorIndicatorEnabled})");

        // 커서 헤일로 윈도우 생성/파괴 — 실제로 바뀐 경우에만 (불필요한 타이머 재설정 방지)
        if (wasCursorEnabled != _config.CursorIndicatorEnabled)
            ApplyCursorConfigChange();

        // 트레이 아이콘 재생성 — 현재 단계의 도형(링/배지) 반영
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
            currentConfig: () => _config,
            updateConfig: newConfig =>
            {
                // **전이 적용은 핫리로드와 같은 함수를 쓴다** (P4 — bug-hunt 3차 C).
                //
                // 종전에는 여기서 적용자를 하나씩 나열했는데, 그 목록에서 **Logger 만 빠져 있었다** —
                // 상세 설정에서 log_level / log_to_file 을 바꾸면 디스크에는 저장되지만 러닝 로거는
                // 그대로였고, Settings.Save 의 mtime self-bump 가 핫리로드까지 막아 **재시작 전까지**
                // 반영되지 않았다. G6 이 "개별 호출자를 고치는 대신 진입점을 하나로" 라고 적은 바로 그
                // 함정이 이 자리에서 실제로 터진 것이라, 나열을 통째로 걷어낸다.
                //
                // 순서 계약은 그대로다 — _config 를 **먼저** 게시해야 ApplyCursorConfigChange /
                // ApplyTrayEnabledTransition 이 필드를 직접 읽는 전제가 성립하고, ClearProfileCache 도
                // 새 인스턴스 게시 뒤에 도는 것이 §N-59 가 세운 순서다.
                AppConfig prev = _config;
                _config = ThemePresets.Apply(newConfig);
                ApplyConfigTransition(prev, _config);

                // 저장 + 병합 시 전이 재적용까지 (확정 #1·#15·#28·#47). 위 적용은 병합 **전** 값으로
                // 돌았으므로, 디스크에서 새 값이 들어왔으면 헬퍼가 그 차이만 다시 적용한다.
                SaveAndSync();
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
