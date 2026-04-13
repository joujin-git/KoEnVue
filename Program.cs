using System.Runtime.InteropServices;
using KoEnVue.Config;
using KoEnVue.Detector;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.UI;
using KoEnVue.Utils;

namespace KoEnVue;

/// <summary>
/// 앱 진입점 + Win32 메시지 루프 + 2-스레드 모델 + 이벤트 파이프라인.
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
    private static volatile bool _indicatorVisible;

    // 포그라운드 윈도우 + 앱별 위치 (메인 스레드 전용)
    private static IntPtr _lastForegroundHwnd;
    private static string _currentProcessName = "";
    private static readonly Dictionary<IntPtr, (int x, int y)> _hwndPositions = [];

    // 라이프사이클
    private static Mutex? _mutex;
    private static bool _stopping;

    // 윈도우 클래스명 (P3: 매직 스트링 금지)
    private const string MainClassName = "KoEnVueMain";

    // 핫키 ID (P3: 매직 넘버 금지)
    private const int HOTKEY_TOGGLE_VISIBILITY = 1;
    private const int HOTKEY_COUNT = 1;

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

        Logger.Debug($"Config: TrayEnabled={_config.TrayEnabled}, DisplayMode={_config.DisplayMode}, EventDisplayDurationMs={_config.EventDisplayDurationMs}, PollIntervalMs={_config.PollIntervalMs}");
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

        // 9c. 시작 프로그램 태스크 경로 동기화 (exe 이동 감지 → 재등록, 백그라운드)
        Tray.SyncStartupPathAsync();

        // 10. 감지 스레드 시작
        StartDetectionThread();

        // 11. IME 이벤트 훅 등록
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

            case AppMessages.WM_POSITION_UPDATED:
                HandlePositionUpdated(wParam);
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

            // === 오버레이 드래그 ===

            case Win32Constants.WM_NCHITTEST:
                if (hwnd == _hwndOverlay)
                    return Win32Constants.HTCAPTION;  // 본체 드래그 가능
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case Win32Constants.WM_MOVING:
                if (hwnd == _hwndOverlay)
                {
                    RECT movingRect = Marshal.PtrToStructure<RECT>(lParam);
                    if (Overlay.HandleMoving(ref movingRect, _lastImeState, _config))
                    {
                        Marshal.StructureToPtr(movingRect, lParam, false);
                        return (IntPtr)1;
                    }
                }
                return IntPtr.Zero;

            case Win32Constants.WM_ENTERSIZEMOVE:
                if (hwnd == _hwndOverlay)
                    Overlay.BeginDrag(_config);
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

    private static void HandleImeStateChanged(ImeState newState)
    {
        _lastImeState = newState;
        Logger.Debug($"IME state: {newState}");

        // 트레이 아이콘은 항상 IME 상태 반영
        if (_config.TrayEnabled)
            Tray.UpdateState(newState, _config);

        if (_lastForegroundHwnd == IntPtr.Zero) return;

        if (_config.DisplayMode == DisplayMode.Always || _config.EventTriggers.OnImeChange)
        {
            _indicatorVisible = true;
            var (x, y) = GetAppPosition();
            Animation.TriggerShow(x, y, newState, _config, imeChanged: true);
        }
    }

    private static void HandleFocusChanged(IntPtr newHwndFocus)
    {
        Logger.Debug($"Focus changed: 0x{newHwndFocus:X}");

        if (_lastForegroundHwnd == IntPtr.Zero) return;

        if (_config.DisplayMode == DisplayMode.Always || _config.EventTriggers.OnFocusChange)
        {
            _indicatorVisible = true;
            var (x, y) = GetAppPosition();
            Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
        }
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

        // 시스템 입력 프로세스(시작 메뉴 ↔ 검색 창)는 하나의 HWND를 모드별로 재사용하면서
        // 시각적 rect만 바꾼다. 감지 스레드가 rect 변화 기반으로 이 메시지를 다시 보낸 경우
        // foregroundChanged가 false여도 위치를 재계산해 실제 시각 rect에 맞춰야 한다.
        bool sysInput = DefaultConfig.IsSystemInputProcess(_currentProcessName);

        if (foregroundChanged || wasHidden || sysInput)
        {
            _indicatorVisible = true;
            var (x, y) = GetAppPosition();
            Logger.Info($"PositionUpdated: process={_currentProcessName}, pos=({x},{y}), saved={_config.IndicatorPositions.Count}");
            Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
        }
        // 같은 앱 내 윈도우 이동 — 플로팅 인디케이터는 위치 고정이므로 무시
    }

    /// <summary>
    /// 오버레이 드래그 종료 → 새 위치를 현재 앱에 저장.
    /// 시스템 입력 프로세스(시작 메뉴, 검색 창)는 z-band 한계로 창 위에 띄울 수 없어
    /// 사용자가 드래그해 옮긴 위치가 가려지면 다시 잡을 수 없게 된다.
    /// 저장하지 않고 항상 기본 위치를 사용한다.
    /// </summary>
    private static void HandleOverlayDragEnd()
    {
        var (x, y) = Overlay.EndDrag();

        if (DefaultConfig.IsSystemInputProcess(_currentProcessName))
        {
            Logger.Debug($"Skip saving indicator position for system input process: {_currentProcessName}");
            Overlay.Show(x, y, _lastImeState, _config);
            return;
        }

        // 런타임 hwnd별 위치 저장
        if (_lastForegroundHwnd != IntPtr.Zero)
            _hwndPositions[_lastForegroundHwnd] = (x, y);
        // config 프로세스명별 위치 저장 (영구)
        if (_currentProcessName.Length > 0)
        {
            var positions = new Dictionary<string, int[]>(_config.IndicatorPositions)
            {
                [_currentProcessName] = [x, y]
            };
            _config = _config with { IndicatorPositions = positions };
            Settings.Save(_config);
            Logger.Debug($"Saved indicator position for {_currentProcessName}: ({x}, {y})");
        }
        // 새 위치의 모니터 DPI로 리소스 재생성
        Overlay.Show(x, y, _lastImeState, _config);
    }

    /// <summary>
    /// 현재 앱의 저장 위치 반환. 없으면 기본 위치.
    /// 시스템 입력 프로세스는 항상 기본 위치 — 저장 위치를 무시 (z-band 가시성 보장).
    /// </summary>
    private static (int x, int y) GetAppPosition()
    {
        // 시스템 입력 프로세스: 저장 위치 우회
        if (DefaultConfig.IsSystemInputProcess(_currentProcessName))
            return Overlay.GetDefaultPosition(_lastForegroundHwnd, _currentProcessName, _config);

        // 1. 런타임 hwnd별 위치 (세션 내 창별 구분)
        if (_lastForegroundHwnd != IntPtr.Zero
            && _hwndPositions.TryGetValue(_lastForegroundHwnd, out var hwndPos))
        {
            return hwndPos;
        }
        // 2. config 프로세스명별 위치 (영구 저장)
        if (_currentProcessName.Length > 0
            && _config.IndicatorPositions.TryGetValue(_currentProcessName, out int[]? pos)
            && pos.Length >= 2)
        {
            return (pos[0], pos[1]);
        }
        // 3. 기본 위치 (포그라운드 창 모니터 기준, config 기본 위치 적용)
        return Overlay.GetDefaultPosition(_lastForegroundHwnd, _currentProcessName, _config);
    }

    private static void HandleConfigChanged()
    {
        _config = Settings.Load();
        Logger.SetLevel(_config.LogLevel);
        Logger.Initialize(_config);
        I18n.Load(_config.Language);
        Settings.ClearProfileCache();
        Overlay.HandleConfigChanged(_config);
        Logger.Info("Config reloaded");
    }

    private static void HideOverlay()
    {
        // forceHidden: Always 모드에서도 Idle이 아닌 완전 숨김으로 전환.
        // 시스템 필터(바탕화면/작업 표시줄), 핫키/트레이 토글 OFF 모두
        // "실제로 사라져야 하는" 의도이므로 Always 모드의 dim-idle 유지를 우회.
        Animation.TriggerHide(_config, forceHidden: true);
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
            case Win32Constants.WM_CONTEXTMENU:
                Tray.ShowMenu(_hwndMain, _config);
                break;
            case Win32Constants.WM_LBUTTONUP:
                if (_config.TrayClickAction == TrayClickAction.Toggle)
                {
                    _indicatorVisible = !_indicatorVisible;
                    if (!_indicatorVisible) HideOverlay();
                }
                break;
        }
    }

    private static void HandleHotkey(int hotkeyId)
    {
        Logger.Debug($"Hotkey pressed: {hotkeyId}");
        if (hotkeyId == HOTKEY_TOGGLE_VISIBILITY)
        {
            _indicatorVisible = !_indicatorVisible;
            if (!_indicatorVisible) HideOverlay();
        }
    }

    private static void HandleMenuCommand(int commandId)
    {
        Tray.HandleMenuCommand(commandId, _config, _hwndMain,
            updateConfig: newConfig =>
            {
                _config = newConfig;
                Overlay.HandleConfigChanged(_config);
                // HandleConfigChanged는 리소스만 재생성하고 UpdateLayeredWindow를 호출하지 않으므로
                // 인디가 가시 상태라면 새 크기/색상이 즉시 화면에 반영되도록 명시적 재렌더.
                if (_indicatorVisible)
                    Overlay.UpdateColor(_lastImeState, _config);
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
        Overlay.HandleDpiChanged(_config);
    }

    private static void HandleDisplayChange()
    {
        Logger.Info("Display changed");
        Overlay.HandleDpiChanged(_config);

        if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
        {
            var (x, y) = GetAppPosition();
            Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
        }
    }

    private static void HandleSettingChange()
    {
        if (_config.Theme == Theme.System)
        {
            _config = ThemePresets.Apply(_config);
            Overlay.HandleConfigChanged(_config);
        }

        if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
        {
            var (x, y) = GetAppPosition();
            Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
        }
    }

    private static void HandleDpiChanged(IntPtr wParam, IntPtr lParam)
    {
        // wParam: HIWORD=newDpiY, LOWORD=newDpiX
        // lParam: RECT* (새 DPI에 맞는 권장 크기/위치)
        Overlay.HandleDpiChanged(_config);
    }

    // ================================================================
    // 2-스레드 모델
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
        string lastForegroundProcessName = string.Empty;
        RECT lastSystemInputFrame = default;
        bool lastFiltered = false;
        ImeState lastImeState = ImeState.English;
        AppConfig lastAppConfig = _config;
        int pollCount = 0;

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

            uint threadId = User32.GetWindowThreadProcessId(hwndForeground, out _);

            // GUITHREADINFO로 hwndFocus 획득
            GUITHREADINFO gti = default;
            gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();
            User32.GetGUIThreadInfo(threadId, ref gti);
            IntPtr hwndFocus = gti.hwndFocus;

            // 콘솔 호스트(conhost) 앱은 hwndFocus가 0 — 포그라운드 윈도우로 대체
            if (hwndFocus == IntPtr.Zero)
            {
                string fgClass = WindowProcessInfo.GetClassName(hwndForeground);
                if (fgClass.Equals(Win32Constants.ConsoleWindowClass, StringComparison.OrdinalIgnoreCase))
                    hwndFocus = hwndForeground;
            }

            // 2. 앱별 프로필 + 시스템 필터 (매 폴링 평가 — 단순함이 정확성을 보장)
            //    - 같은 앱으로 복귀(데스크톱 → 같은 앱) 시 인디 표시
            AppConfig? resolved = Settings.ResolveForApp(_config, hwndForeground);
            bool currentlyFiltered = (resolved is null)
                || SystemFilter.ShouldHide(hwndForeground, hwndFocus, resolved);

            if (currentlyFiltered)
            {
                // 필터 진입 시에만 숨김 메시지 전송 (중복 메시지 억제)
                if (!lastFiltered && _indicatorVisible)
                    User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                        IntPtr.Zero, IntPtr.Zero);
                lastHwndForeground = hwndForeground;
                lastHwndFocus = hwndFocus;
                lastFiltered = true;
                lastSystemInputFrame = default;
                continue;
            }

            AppConfig appConfig = resolved!;
            lastAppConfig = appConfig;

            // hwnd 변경 시에만 프로세스 이름 캐시 갱신 (폴링당 Process.GetProcessById 호출 회피)
            if (hwndForeground != lastHwndForeground)
            {
                lastForegroundProcessName = WindowProcessInfo.GetProcessName(hwndForeground);
                lastSystemInputFrame = default;
            }

            // 필터 해소 또는 포그라운드 변경 → 위치/포커스 갱신
            bool foregroundChanged = (hwndForeground != lastHwndForeground) || lastFiltered;
            lastFiltered = false;

            // 시스템 입력 프로세스(시작 메뉴 ↔ 검색 창)는 하나의 HWND를 모드별로 재사용하면서
            // rect만 바꾸기 때문에 hwnd 비교만으로는 전환을 감지할 수 없다. 같은 hwnd라도
            // 시각적 프레임이 달라졌다면 포그라운드 변경으로 취급해 위치를 갱신한다.
            if (DefaultConfig.IsSystemInputProcess(lastForegroundProcessName)
                && Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT currentFrame))
            {
                if (currentFrame.Left != lastSystemInputFrame.Left
                    || currentFrame.Top != lastSystemInputFrame.Top
                    || currentFrame.Right != lastSystemInputFrame.Right
                    || currentFrame.Bottom != lastSystemInputFrame.Bottom)
                {
                    foregroundChanged = true;
                    lastSystemInputFrame = currentFrame;
                }
            }

            // 3. 포그라운드 변경 시 위치 갱신 (플로팅 인디케이터 — 윈도우 이동 추적 불필요)
            if (foregroundChanged)
            {
                User32.PostMessageW(_hwndMain, AppMessages.WM_POSITION_UPDATED,
                    hwndForeground, IntPtr.Zero);
                lastHwndForeground = hwndForeground;
            }

            // 4. IME 상태 감지
            ImeState currentIme = ImeStatus.Detect(hwndFocus, threadId, appConfig);
            if (currentIme != lastImeState || foregroundChanged)
            {
                lastImeState = currentIme;
                User32.PostMessageW(_hwndMain, AppMessages.WM_IME_STATE_CHANGED,
                    (IntPtr)(int)currentIme, IntPtr.Zero);
            }

            // 5. 포커스 변경 감지
            if (hwndFocus != lastHwndFocus || foregroundChanged)
            {
                lastHwndFocus = hwndFocus;
                User32.PostMessageW(_hwndMain, AppMessages.WM_FOCUS_CHANGED,
                    hwndFocus, IntPtr.Zero);
            }
        }
    }

    // ================================================================
    // 핫키 등록/해제
    // ================================================================

    private static void RegisterHotkeys()
    {
        RegisterSingleHotkey(HOTKEY_TOGGLE_VISIBILITY, _config.HotkeyToggleVisibility);
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

        // 3. 파일 로거 종료
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
