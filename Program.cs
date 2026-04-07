using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
    private static string? _configFilePath;

    // 윈도우 클래스명 (P3: 매직 스트링 금지)
    private const string MainClassName = "KoEnVueMain";

    // 핫키 ID (P3: 매직 넘버 금지)
    private const int HOTKEY_TOGGLE_VISIBILITY = 1;
    private const int HOTKEY_CYCLE_STYLE = 2;
    private const int HOTKEY_CYCLE_POSITION = 3;
    private const int HOTKEY_CYCLE_DISPLAY = 4;
    private const int HOTKEY_OPEN_SETTINGS = 5;
    private const int HOTKEY_COUNT = 5;

    // 설정 파일명
    private const string ConfigFileName = "config.json";
    private const string AppDataFolderName = "KoEnVue";

    // ================================================================
    // 진입점
    // ================================================================

    static void Main()
    {
        // 1. 이전 트레이 찌꺼기 정리 (크래시 복구)
        CleanupPreviousTrayIcon();

        // 2. 다중 인스턴스 체크
        if (!TryAcquireMutex()) return;

        // 3. 설정 로드
        _config = LoadConfig();

        // 4. 로거 초기화
        Logger.SetLevel(_config.LogLevel);
        Logger.Info("KoEnVue starting");

        // 5. 메인 스레드 COM STA 초기화 (메시지 루프 + WinEventHook + SystemFilter VDM)
        Ole32.CoInitializeEx(IntPtr.Zero, Win32Constants.COINIT_APARTMENTTHREADED);

        // 6. SystemFilter static constructor 강제 실행 (메인 스레드에서 VDM COM 생성)
        _ = SystemFilter.ShouldHide(IntPtr.Zero, IntPtr.Zero, _config);

        // 7. 윈도우 클래스 등록
        RegisterWindowClasses();

        // 8. 메인 윈도우 생성 (메시지 전용, 화면 미표시)
        _hwndMain = CreateMainWindow();

        // 9. 오버레이 윈도우 생성
        _hwndOverlay = CreateOverlayWindow();

        // 9a. 렌더링 + 애니메이션 초기화
        Overlay.Initialize(_hwndOverlay, _config);
        Animation.Initialize(_hwndMain, _hwndOverlay, _config);

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
    // 설정 로드 (Phase 06 Settings.cs 전까지 인라인)
    // ================================================================

    private static AppConfig LoadConfig()
    {
        // 탐색 순서: %APPDATA%\KoEnVue\config.json → exe dir → defaults
        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppDataFolderName, ConfigFileName),
            Path.Combine(AppContext.BaseDirectory, ConfigFileName),
        ];

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;

                string json = File.ReadAllText(path);
                AppConfig? config = JsonSerializer.Deserialize(
                    json, AppConfigJsonContext.Default.AppConfig);

                if (config is not null)
                {
                    _configFilePath = path;
                    Logger.Info($"Config loaded from {path}");
                    return config;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load config from {path}: {ex.Message}");
            }
        }

        Logger.Info("Using default config");
        return new AppConfig();
    }

    // ================================================================
    // 윈도우 클래스 등록 + 윈도우 생성
    // ================================================================

    private static unsafe void RegisterWindowClasses()
    {
        // 메인 윈도우 클래스
        var mainClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            lpszClassName = MainClassName,
        };
        User32.RegisterClassExW(ref mainClass);

        // 오버레이 윈도우 클래스
        var overlayClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            lpszClassName = _config.Advanced.OverlayClassName,
        };
        User32.RegisterClassExW(ref overlayClass);
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
        while (User32.GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
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

        // Phase 05: Tray 아이콘/툴팁 갱신
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
        _config = LoadConfig();
        Logger.SetLevel(_config.LogLevel);
        CaretTracker.ClearCache();
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
        // Phase 05: 트레이 아이콘 클릭/메뉴
    }

    private static void HandleHotkey(int hotkeyId)
    {
        Logger.Debug($"Hotkey pressed: {hotkeyId}");
        // Phase 05: 핫키 동작 실행
    }

    private static void HandleMenuCommand(int commandId)
    {
        // Phase 05: 트레이 메뉴 명령 처리
    }

    private static void HandlePowerResume()
    {
        Logger.Info("Power resumed");
        // Phase 07: IME 재감지, DPI/rcWork 재조회
    }

    private static void HandleDisplayChange()
    {
        Logger.Info("Display changed");
        Overlay.HandleDpiChanged(_config);
    }

    private static void HandleSettingChange()
    {
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
        int pollCount = 0;
        DateTime lastConfigMtime = DateTime.MinValue;

        while (!_stopping)
        {
            Thread.Sleep(_config.PollIntervalMs);
            pollCount++;

            // 0. config.json 변경 감지 (~5초마다)
            if (pollCount % DefaultConfig.ConfigCheckIntervalPolls == 0)
                CheckConfigFileChange(ref lastConfigMtime);

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

            // 2. 시스템 필터 (포그라운드 변경 시에만 실행 — 최적화)
            if (hwndForeground != lastHwndForeground)
            {
                lastHwndForeground = hwndForeground;

                if (SystemFilter.ShouldHide(hwndForeground, hwndFocus, _config))
                {
                    if (_indicatorVisible)
                        User32.PostMessage(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                            IntPtr.Zero, IntPtr.Zero);
                    continue;
                }
            }

            // 3. 포커스 변경 감지
            if (hwndFocus != lastHwndFocus)
            {
                lastHwndFocus = hwndFocus;
                User32.PostMessage(_hwndMain, AppMessages.WM_FOCUS_CHANGED,
                    hwndFocus, IntPtr.Zero);
            }

            // 4. IME 상태 감지
            ImeState currentIme = ImeStatus.Detect(hwndFocus, threadId);
            if (currentIme != lastImeState)
            {
                lastImeState = currentIme;
                User32.PostMessage(_hwndMain, AppMessages.WM_IME_STATE_CHANGED,
                    (IntPtr)(int)currentIme, IntPtr.Zero);
            }

            // 5. 캐럿 위치 추적 (이벤트 발생 시 또는 Always 모드)
            if (_needCaretUpdate || _config.DisplayMode == DisplayMode.Always)
            {
                string procName = GetProcessName(processId);
                var result = CaretTracker.GetCaretPosition(
                    hwndFocus, threadId, procName, _config);

                if (result is { } caret)
                {
                    _lastCaretH = caret.h;  // volatile int 쓰기
                    User32.PostMessage(_hwndMain, AppMessages.WM_CARET_UPDATED,
                        (IntPtr)caret.x, (IntPtr)caret.y);
                }

                _needCaretUpdate = false;
            }
        }
    }

    private static void CheckConfigFileChange(ref DateTime lastMtime)
    {
        if (_configFilePath is null) return;

        try
        {
            DateTime mtime = File.GetLastWriteTimeUtc(_configFilePath);
            if (mtime != lastMtime)
            {
                lastMtime = mtime;
                User32.PostMessage(_hwndMain, AppMessages.WM_CONFIG_CHANGED,
                    IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 파일 접근 실패 시 무시
        }
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0) return string.Empty;

        try
        {
            using var proc = Process.GetProcessById((int)processId);
            return proc.ProcessName;
        }
        catch
        {
            return string.Empty;
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

        // Phase 07에서 IUIAutomation 인스턴스 생성 + 큐 기반 요청/응답 구현

        while (!_stopping)
            Thread.Sleep(DefaultConfig.UiaLoopIntervalMs);

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
            if (mod.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_CONTROL;
            else if (mod.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_ALT;
            else if (mod.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= Win32Constants.MOD_SHIFT;
            else if (mod.Equals("Win", StringComparison.OrdinalIgnoreCase))
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
        else
        {
            return false;  // F1-F12 등은 Phase 05에서 확장
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

        // 3. 애니메이션 + 렌더링 리소스 해제 (윈도우 파괴 전)
        Animation.Dispose();
        Overlay.Dispose();

        // 4. 오버레이 윈도우 파괴
        if (_hwndOverlay != IntPtr.Zero)
            User32.DestroyWindow(_hwndOverlay);

        // 5. Phase 05: Tray.Remove()

        // 6. Mutex 해제
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        // 7. COM 해제
        Ole32.CoUninitialize();

        Logger.Info("KoEnVue stopped");
    }
}
