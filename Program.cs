using System.Runtime.InteropServices;
using KoEnVue.Config;
using KoEnVue.Detector;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.UI;
using KoEnVue.Utils;

namespace KoEnVue;

/// <summary>
/// 앱 진입점 + Win32 메시지 루프 + 3-스레드 모델 + 이벤트 파이프라인.
/// </summary>
internal static class Program
{
    // ================================================================
    // 전역 상태
    // ================================================================

    // 윈도우 핸들
    private static IntPtr _hwndMain;
    private static IntPtr _hwndOverlay;

    // 스레드 간 공유 상태 (volatile — 원자적 참조/값 교체)
    private static volatile AppConfig _config = null!;
    private static volatile ImeState _lastImeState = ImeState.English;
    private static volatile bool _needCaretUpdate;
    private static volatile bool _indicatorVisible;
    private static volatile int _lastCaretH;  // 감지 스레드 → 메인 스레드

    // 캐럿 위치 (메인 스레드 전용)
    private static int _lastCaretX;
    private static int _lastCaretY;
    private static bool _caretPositionKnown;

    // 라이프사이클
    private static Mutex? _mutex;
    private static bool _stopping;

    // 윈도우 클래스명 (P3: 매직 스트링 금지)
    private const string MainClassName = "KoEnVueMain";

    // 핫키 ID (P3: 매직 넘버 금지)
    private const int HOTKEY_TOGGLE_VISIBILITY = 1;
    private const int HOTKEY_CYCLE_STYLE = 2;
    private const int HOTKEY_CYCLE_POSITION = 3;
    private const int HOTKEY_CYCLE_DISPLAY = 4;
    private const int HOTKEY_OPEN_SETTINGS = 5;
    private const int HOTKEY_COUNT = 5;

    // 설정 파일명 → DefaultConfig에서 참조

    // ================================================================
    // 진입점
    // ================================================================

    static void Main()
    {
        try
        {
            MainImpl();
        }
        catch (Exception ex)
        {
            // 비상 로깅 — Logger 초기화 전이면 파일에 직접 기록
            try { File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "koenvue_crash.txt"),
                $"[{DateTime.Now:HH:mm:ss.fff}] FATAL: {ex}\n"); } catch { }
            Logger.Error($"Fatal: {ex}");
            Logger.Shutdown();
        }
    }

    static void MainImpl()
    {
        // 1. 이전 트레이 찌꺼기 정리 (크래시 복구)
        CleanupPreviousTrayIcon();

        // 2. 다중 인스턴스 체크
        if (!TryAcquireMutex()) return;

        // 3. 설정 로드
        _config = Settings.Load();

        // 4. 로거 + I18n 초기화
        Logger.SetLevel(_config.LogLevel);
        Logger.Initialize(_config);

        Logger.Debug($"Config: TrayEnabled={_config.TrayEnabled}, DisplayMode={_config.DisplayMode}, EventDisplayDurationMs={_config.EventDisplayDurationMs}, IndicatorStyle={_config.IndicatorStyle}, PollIntervalMs={_config.PollIntervalMs}");
        I18n.Load(_config.Language);
        Logger.Info("KoEnVue starting");

        // 5. 메인 스레드 COM STA 초기화 (메시지 루프 + WinEventHook + SystemFilter VDM)
        Ole32.CoInitializeEx(IntPtr.Zero, Win32Constants.COINIT_APARTMENTTHREADED);

        // 6. SystemFilter static constructor 강제 실행 (메인 스레드에서 VDM COM 생성)
        _ = SystemFilter.ShouldHide(IntPtr.Zero, IntPtr.Zero, _config);

        // 7. 윈도우 클래스 등록
        Logger.Debug("Registering window classes");
        RegisterWindowClasses();

        // 8. 메인 윈도우 생성 (메시지 전용, 화면 미표시)
        Logger.Debug("Creating main window");
        _hwndMain = CreateMainWindow();

        // 9. 오버레이 윈도우 생성
        Logger.Debug("Creating overlay window");
        _hwndOverlay = CreateOverlayWindow();

        // 9a. 렌더링 + 애니메이션 초기화
        Logger.Debug("Initializing overlay rendering");
        Overlay.Initialize(_hwndOverlay, _config);
        Logger.Debug("Initializing animation");
        Animation.Initialize(_hwndMain, _hwndOverlay, _config);

        // 9b. 트레이 아이콘 초기화
        Tray.Initialize(_hwndMain, _lastImeState, _config);

        // 10. 감지 스레드 시작
        StartDetectionThread();

        // 11. UIA 스레드 시작 (Phase 07 스텁)
        StartUiaThread();

        // 12. IME 이벤트 훅 등록
        ImeStatus.RegisterHook(_hwndMain);

        // 13. 핫키 등록
        if (_config.HotkeysEnabled)
            RegisterHotkeys();

        // 14. 종료 핸들러
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        Logger.Info("Initialization complete, entering message loop");

        // 15. 메인 메시지 루프
        RunMessageLoop();
    }

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
            Win32Constants.WS_EX_LAYERED | Win32Constants.WS_EX_TRANSPARENT
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
        switch (msg)
        {
            // === 커스텀 메시지 (감지 스레드 → 메인 스레드) ===

            case AppMessages.WM_IME_STATE_CHANGED:
                HandleImeStateChanged((ImeState)(int)wParam);
                return IntPtr.Zero;

            case AppMessages.WM_FOCUS_CHANGED:
                HandleFocusChanged(wParam);
                return IntPtr.Zero;

            case AppMessages.WM_CARET_UPDATED:
                HandleCaretUpdated((int)wParam, (int)lParam);
                return IntPtr.Zero;

            case AppMessages.WM_HIDE_INDICATOR:
                HideOverlay();
                return IntPtr.Zero;

            case AppMessages.WM_CONFIG_CHANGED:
                HandleConfigChanged();
                return IntPtr.Zero;

            // === 트레이 ===

            case AppMessages.WM_TRAY_CALLBACK:
                HandleTrayCallback(lParam);
                return IntPtr.Zero;

            // === 타이머 (애니메이션) ===

            case Win32Constants.WM_TIMER:
                HandleTimer(wParam);
                return IntPtr.Zero;

            // === 핫키 ===

            case Win32Constants.WM_HOTKEY:
                HandleHotkey((int)wParam);
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
                HandleSettingChange();
                return IntPtr.Zero;

            case Win32Constants.WM_DPICHANGED:
                HandleDpiChanged(wParam, lParam);
                return IntPtr.Zero;

            case Win32Constants.WM_COMMAND:
                HandleMenuCommand((int)wParam);
                return IntPtr.Zero;

            case Win32Constants.WM_DESTROY:
                if (hwnd == _hwndMain)
                    User32.PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // ================================================================
    // 이벤트 핸들러
    // ================================================================

    private static void HandleImeStateChanged(ImeState newState)
    {
        _lastImeState = newState;
        Logger.Debug($"IME state: {newState}");

        // EventTriggers 가드: 트리거 비활성이면 캐럿 갱신 불필요
        if (_config.EventTriggers.OnImeChange)
            _needCaretUpdate = true;

        // 캐럿 위치 미수신 시 표시 건너뜀
        if (!_caretPositionKnown) return;

        // Always 모드이거나 EventTrigger 활성 시 표시 트리거
        if (_config.DisplayMode == DisplayMode.Always || _config.EventTriggers.OnImeChange)
        {
            _indicatorVisible = true;
            Animation.TriggerShow(_lastCaretX, _lastCaretY, _lastCaretH,
                newState, _config, imeChanged: true);
        }

        if (_config.TrayEnabled)
            Tray.UpdateState(newState, _config);
    }

    private static void HandleFocusChanged(IntPtr newHwndFocus)
    {
        Logger.Debug($"Focus changed: 0x{newHwndFocus:X}");

        // EventTriggers 가드
        if (_config.EventTriggers.OnFocusChange)
            _needCaretUpdate = true;

        // 캐럿 위치 미수신 시 표시 건너뜀
        if (!_caretPositionKnown) return;

        if (_config.DisplayMode == DisplayMode.Always || _config.EventTriggers.OnFocusChange)
        {
            _indicatorVisible = true;
            Animation.TriggerShow(_lastCaretX, _lastCaretY, _lastCaretH,
                _lastImeState, _config, imeChanged: false);
        }
    }

    private static void HandleCaretUpdated(int x, int y)
    {
        Logger.Debug($"Caret position: ({x}, {y})");
        _caretPositionKnown = true;
        _lastCaretX = x;
        _lastCaretY = y;
        _indicatorVisible = true;
        Animation.TriggerShow(x, y, _lastCaretH, _lastImeState, _config, imeChanged: false);
    }

    private static void HandleConfigChanged()
    {
        _config = Settings.Load();
        Logger.SetLevel(_config.LogLevel);
        Logger.Initialize(_config);
        I18n.Load(_config.Language);
        CaretTracker.ClearCache();
        Settings.ClearProfileCache();
        Overlay.HandleConfigChanged(_config);
        Logger.Info("Config reloaded");
    }

    private static void HideOverlay()
    {
        Animation.TriggerHide(_config);
        _indicatorVisible = false;
    }

    // --- Phase 04+ 스텁 ---

    private static void HandleTimer(IntPtr timerId)
    {
        Animation.HandleTimer((nuint)(nint)timerId, _config);
    }

    private static void HandleTrayCallback(IntPtr lParam)
    {
        uint mouseEvent = (uint)(lParam.ToInt64() & Win32Constants.LOWORD_MASK);
        switch (mouseEvent)
        {
            case Win32Constants.WM_RBUTTONUP:
                Tray.ShowMenu(_hwndMain, _config);
                break;
            case Win32Constants.WM_LBUTTONUP:
                switch (_config.TrayClickAction)
                {
                    case TrayClickAction.Toggle:
                        _indicatorVisible = !_indicatorVisible;
                        if (!_indicatorVisible) HideOverlay();
                        break;
                    case TrayClickAction.Settings:
                        Settings.OpenSettingsFile();
                        break;
                }
                break;
        }
    }

    private static void HandleHotkey(int hotkeyId)
    {
        Logger.Debug($"Hotkey pressed: {hotkeyId}");
        switch (hotkeyId)
        {
            case HOTKEY_TOGGLE_VISIBILITY:
                _indicatorVisible = !_indicatorVisible;
                if (!_indicatorVisible) HideOverlay();
                break;
            case HOTKEY_CYCLE_STYLE:
                const int indicatorStyleCount = (int)IndicatorStyle.CaretVbar + 1;
                var nextStyle = (IndicatorStyle)(((int)_config.IndicatorStyle + 1) % indicatorStyleCount);
                UpdateConfigAndNotify(c => c with { IndicatorStyle = nextStyle });
                break;
            case HOTKEY_CYCLE_POSITION:
                var nextPos = _config.PositionMode switch
                {
                    PositionMode.Caret => PositionMode.Mouse,
                    PositionMode.Mouse => PositionMode.Fixed,
                    _ => PositionMode.Caret,
                };
                UpdateConfigAndNotify(c => c with { PositionMode = nextPos });
                break;
            case HOTKEY_CYCLE_DISPLAY:
                var nextDisplay = _config.DisplayMode == DisplayMode.OnEvent
                    ? DisplayMode.Always : DisplayMode.OnEvent;
                UpdateConfigAndNotify(c => c with { DisplayMode = nextDisplay });
                break;
            case HOTKEY_OPEN_SETTINGS:
                Settings.OpenSettingsFile();
                break;
        }
    }

    private static void HandleMenuCommand(int commandId)
    {
        Tray.HandleMenuCommand(commandId, _config, _hwndMain,
            updateConfig: newConfig =>
            {
                _config = newConfig;
                Overlay.HandleConfigChanged(_config);
                if (_config.TrayEnabled)
                    Tray.UpdateState(_lastImeState, _config);
                Settings.Save(_config);
            });
    }

    private static void UpdateConfigAndNotify(Func<AppConfig, AppConfig> transform)
    {
        _config = transform(_config);
        Overlay.HandleConfigChanged(_config);
        if (_config.TrayEnabled)
            Tray.UpdateState(_lastImeState, _config);
        Settings.Save(_config);
    }

    private static void HandlePowerResume()
    {
        Logger.Info("Power resumed");
        // IME 재감지 트리거
        _needCaretUpdate = true;
        // DPI/rcWork 재조회
        Overlay.HandleDpiChanged(_config);
    }

    private static void HandleDisplayChange()
    {
        Logger.Info("Display changed");
        Overlay.HandleDpiChanged(_config);

        // 표시 중이면 위치 재계산
        if (_indicatorVisible)
            Animation.TriggerShow(_lastCaretX, _lastCaretY, _lastCaretH,
                _lastImeState, _config, imeChanged: false);
    }

    private static void HandleSettingChange()
    {
        // 고대비 / 시스템 테마 변경 감지
        if (_config.Theme == Theme.System)
        {
            _config = ThemePresets.Apply(_config);
            Overlay.HandleConfigChanged(_config);
        }

        // 작업표시줄 변경 시 표시 중이면 위치 재계산
        if (_indicatorVisible)
            Animation.TriggerShow(_lastCaretX, _lastCaretY, _lastCaretH,
                _lastImeState, _config, imeChanged: false);
    }

    private static void HandleDpiChanged(IntPtr wParam, IntPtr lParam)
    {
        // wParam: HIWORD=newDpiY, LOWORD=newDpiX
        // lParam: RECT* (새 DPI에 맞는 권장 크기/위치)
        Overlay.HandleDpiChanged(_config);
    }

    // ================================================================
    // 3-스레드 모델
    // ================================================================

    // --- 감지 스레드 ---

    private static void StartDetectionThread()
    {
        var thread = new Thread(DetectionLoop)
        {
            IsBackground = true,
            Name = "KoEnVue-Detection",
        };
        thread.Start();
    }

    private static void DetectionLoop()
    {
        IntPtr lastHwndFocus = IntPtr.Zero;
        IntPtr lastHwndForeground = IntPtr.Zero;
        ImeState lastImeState = ImeState.English;
        AppConfig lastAppConfig = _config;
        int pollCount = 0;
        int lastPostedCaretX = int.MinValue;
        int lastPostedCaretY = int.MinValue;

        while (!_stopping)
        {
            Thread.Sleep(_config.PollIntervalMs);
            pollCount++;

            // 0. config.json 변경 감지 (~5초마다)
            if (pollCount % DefaultConfig.ConfigCheckIntervalPolls == 0)
                Settings.CheckConfigFileChange(_hwndMain);

            // 1. 포그라운드 윈도우 확인
            IntPtr hwndForeground = User32.GetForegroundWindow();

            // 자기 자신 무시
            if (hwndForeground == _hwndMain || hwndForeground == _hwndOverlay)
                continue;

            uint threadId = User32.GetWindowThreadProcessId(hwndForeground, out uint processId);

            // GUITHREADINFO로 hwndFocus 획득
            GUITHREADINFO gti = default;
            gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();
            User32.GetGUIThreadInfo(threadId, ref gti);
            IntPtr hwndFocus = gti.hwndFocus;

            // 2. 앱별 프로필 + 시스템 필터 (포그라운드 변경 시에만 — 최적화)
            AppConfig appConfig = lastAppConfig;
            bool foregroundChanged = false;
            if (hwndForeground != lastHwndForeground)
            {
                lastHwndForeground = hwndForeground;

                // 앱별 프로필 적용 (LRU 캐싱됨)
                AppConfig? resolved = Settings.ResolveForApp(_config, hwndForeground);
                if (resolved is null)
                {
                    // enabled: false → 이 앱에서 인디케이터 비활성화
                    if (_indicatorVisible)
                        User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                            IntPtr.Zero, IntPtr.Zero);
                    continue;
                }
                appConfig = resolved;
                lastAppConfig = appConfig;

                if (SystemFilter.ShouldHide(hwndForeground, hwndFocus, appConfig))
                {
                    if (_indicatorVisible)
                        User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                            IntPtr.Zero, IntPtr.Zero);
                    continue;
                }

                foregroundChanged = true;
            }

            // 3. 포커스 변경 감지
            // foregroundChanged: 숨김 창(데스크톱 등)을 거쳐 돌아오면
            //   hwndFocus가 같아도 포커스 이벤트 + 캐럿 폴링 필요
            bool focusChanged = false;
            if (hwndFocus != lastHwndFocus || foregroundChanged)
            {
                lastHwndFocus = hwndFocus;
                User32.PostMessageW(_hwndMain, AppMessages.WM_FOCUS_CHANGED,
                    hwndFocus, IntPtr.Zero);
                focusChanged = true;
            }

            // 4. IME 상태 감지 (3-param: 앱 프로필 detection_method 반영)
            ImeState currentIme = ImeStatus.Detect(hwndFocus, threadId, appConfig);
            if (currentIme != lastImeState)
            {
                lastImeState = currentIme;
                User32.PostMessageW(_hwndMain, AppMessages.WM_IME_STATE_CHANGED,
                    (IntPtr)(int)currentIme, IntPtr.Zero);
            }

            // 5. 캐럿 위치 추적
            // - IME/포커스 이벤트 시: 즉시 폴링 + 무조건 전달
            // - OnCaretMove 활성 시: 항상 폴링, 임계값 이상 이동 시에만 전달
            // - Always 모드: 항상 폴링 + 무조건 전달
            bool eventTriggered = _needCaretUpdate || focusChanged;
            bool shouldPollCaret = eventTriggered
                || appConfig.DisplayMode == DisplayMode.Always
                || appConfig.EventTriggers.OnCaretMove;

            if (shouldPollCaret)
            {
                long pollStart = Environment.TickCount64;
                string procName = SystemFilter.GetProcessName(processId);
                var result = CaretTracker.GetCaretPosition(
                    hwndFocus, threadId, procName, appConfig);

                if (result is { } caret)
                {
                    // 이벤트/Always: 무조건 전달. OnCaretMove: 임계값 초과 시만 전달.
                    bool shouldPost = eventTriggered
                        || appConfig.DisplayMode == DisplayMode.Always;

                    if (!shouldPost && appConfig.EventTriggers.OnCaretMove)
                    {
                        int dx = caret.x - lastPostedCaretX;
                        int dy = caret.y - lastPostedCaretY;
                        int threshold = appConfig.CaretMoveThresholdPx;
                        shouldPost = (dx * dx + dy * dy) >= threshold * threshold;
                    }

                    if (shouldPost)
                    {
                        _lastCaretH = caret.h;  // volatile int 쓰기
                        User32.PostMessageW(_hwndMain, AppMessages.WM_CARET_UPDATED,
                            (IntPtr)caret.x, (IntPtr)caret.y);
                        lastPostedCaretX = caret.x;
                        lastPostedCaretY = caret.y;
                    }

                    // 디버그 오버레이 데이터 전달
                    if (appConfig.Advanced.DebugOverlay)
                    {
                        long pollingMs = Environment.TickCount64 - pollStart;
                        string className = SystemFilter.GetClassName(hwndFocus);
                        IntPtr hMon = DpiHelper.GetMonitorFromPoint(caret.x, caret.y);
                        (uint dpiX, _) = DpiHelper.GetRawDpi(hMon);
                        Overlay.SetDebugInfo(new DebugInfo(
                            caret.method, caret.x, caret.y, dpiX, pollingMs, className));
                    }
                }

                _needCaretUpdate = false;
            }
        }
    }

    // --- UIA 스레드 (Phase 07 스텁) ---

    private static void StartUiaThread()
    {
        var thread = new Thread(UiaLoop)
        {
            IsBackground = true,
            Name = "KoEnVue-UIA",
        };
        thread.Start();
    }

    private static void UiaLoop()
    {
        Ole32.CoInitializeEx(IntPtr.Zero, Win32Constants.COINIT_APARTMENTTHREADED);

        UiaClient.Initialize();
        UiaClient.ProcessRequests();

        UiaClient.Shutdown();
        Ole32.CoUninitialize();
    }

    // ================================================================
    // 핫키 등록/해제
    // ================================================================

    private static void RegisterHotkeys()
    {
        RegisterSingleHotkey(HOTKEY_TOGGLE_VISIBILITY, _config.HotkeyToggleVisibility);
        RegisterSingleHotkey(HOTKEY_CYCLE_STYLE, _config.HotkeyCycleStyle);
        RegisterSingleHotkey(HOTKEY_CYCLE_POSITION, _config.HotkeyCyclePosition);
        RegisterSingleHotkey(HOTKEY_CYCLE_DISPLAY, _config.HotkeyCycleDisplay);
        RegisterSingleHotkey(HOTKEY_OPEN_SETTINGS, _config.HotkeyOpenSettings);
    }

    private static void RegisterSingleHotkey(int id, string hotkeyString)
    {
        if (!ParseHotkey(hotkeyString, out uint modifiers, out uint vk))
        {
            Logger.Warning($"Invalid hotkey format: {hotkeyString}");
            return;
        }

        if (!User32.RegisterHotKey(_hwndMain, id, modifiers | Win32Constants.MOD_NOREPEAT, vk))
            Logger.Warning($"Failed to register hotkey: {hotkeyString}");
    }

    private static void UnregisterHotkeys()
    {
        for (int id = 1; id <= HOTKEY_COUNT; id++)
            User32.UnregisterHotKey(_hwndMain, id);
    }

    // 핫키 파싱 상수 (P3: 매직 스트링 금지)
    private const string ModCtrl = "Ctrl";
    private const string ModAlt = "Alt";
    private const string ModShift = "Shift";
    private const string ModWin = "Win";
    private const string FKeyPrefix = "F";
    private const int FKeyMin = 1;
    private const int FKeyMax = 12;

    private static bool ParseHotkey(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrEmpty(hotkey)) return false;

        string[] parts = hotkey.Split('+');
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string mod = parts[i].Trim();
            if (mod.Equals(ModCtrl, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_CONTROL;
            else if (mod.Equals(ModAlt, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_ALT;
            else if (mod.Equals(ModShift, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_SHIFT;
            else if (mod.Equals(ModWin, StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_WIN;
            else
                return false;
        }

        string keyStr = parts[^1].Trim();
        if (keyStr.Length == 1)
        {
            char c = char.ToUpperInvariant(keyStr[0]);
            if (c is >= 'A' and <= 'Z')
                vk = (uint)c;          // A-Z → 0x41-0x5A
            else if (c is >= '0' and <= '9')
                vk = (uint)c;          // 0-9 → 0x30-0x39
            else
                return false;
        }
        else if (keyStr.StartsWith(FKeyPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(keyStr.AsSpan(1), out int fNum)
            && fNum >= FKeyMin && fNum <= FKeyMax)
        {
            vk = (uint)(Win32Constants.VK_F1 + fNum - 1);
        }
        else
        {
            return false;
        }

        return vk != 0;
    }

    // ================================================================
    // 종료 처리
    // ================================================================

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        _stopping = true;

        // 1. IME 훅 해제
        ImeStatus.UnregisterHook();

        // 2. 핫키 해제
        UnregisterHotkeys();

        // 3. UIA 클라이언트 종료
        UiaClient.Shutdown();

        // 3a. 파일 로거 종료
        Logger.Shutdown();

        // 4. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
        Animation.Dispose();
        Overlay.Dispose();

        // 5. 오버레이 윈도우 파괴
        if (_hwndOverlay != IntPtr.Zero)
            User32.DestroyWindow(_hwndOverlay);

        // 6. 트레이 아이콘 제거
        Tray.Remove();

        // 7. Mutex 해제 (Dispose만 — 프로세스 종료 시 OS가 자동 해제.
        //    ReleaseMutex는 소유 스레드에서만 호출 가능하나 ProcessExit는 다른 스레드일 수 있음)
        _mutex?.Dispose();

        // 8. COM 해제
        Ole32.CoUninitialize();

        Logger.Info("KoEnVue stopped");
    }
}
