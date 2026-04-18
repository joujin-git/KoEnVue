using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
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
///   <item><c>Program.cs</c> — 진입점, MainImpl, 메시지 루프, WndProc, 이벤트 핸들러, 감지 스레드</item>
///   <item><c>Program.Bootstrap.cs</c> — 다중 인스턴스, 윈도우 클래스/생성, 핫키 등록·해제·파싱, ProcessExit</item>
/// </list>
/// </para>
/// </summary>
internal static partial class Program
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

    // CAPS LOCK 토글 캐시 (메인 스레드 전용 — TIMER_ID_CAPS 폴러가 200ms마다 GetKeyState 비교)
    private static bool _lastCapsLockState;

    // UpdateChecker 백그라운드 스레드 → 메인 스레드 페이로드 전달.
    // PostMessage 의 wParam/lParam 으로 객체를 직접 못 보내므로 volatile 참조로 게시한다.
    private static volatile UpdateInfo? _pendingUpdate;

    // 라이프사이클 (감지 스레드에서 읽고 OnProcessExit에서 씀 → volatile)
    private static volatile bool _stopping;

    // 윈도우 클래스명 (P3: 매직 스트링 금지)
    private const string MainClassName = "KoEnVueMain";

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
            // 비상 로깅 — Logger 초기화 전이면 파일에 직접 기록. 기록 실패 시에도 앱 종료 경로라
            // 추가 복구할 수 없으므로 I/O·권한·보안 실패를 흡수. 로직 버그는 전파.
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "koenvue_crash.txt"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] FATAL: {ex}\n");
            }
            catch (Exception inner) when (inner is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                _ = inner;
            }
            Logger.Error($"Fatal: {ex}");
            Logger.Shutdown();
        }
    }

    static void MainImpl()
    {
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

        // 3. 설정 로드
        _config = Settings.Load();

        // 4. 로거 + I18n 초기화
        Logger.SetLevel(_config.LogLevel);
        Logger.Initialize(_config.LogToFile, _config.LogFilePath, _config.LogMaxSizeMb);

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
        Tray.SyncStartupPathAsync();

        // 9d. CAPS LOCK 폴링 타이머 시작 (200ms, 메인 스레드)
        //     GetKeyState는 calling thread 입력 상태를 읽기 때문에 메시지 큐가 있는 메인 스레드에서만
        //     신뢰할 수 있다 → 감지 스레드(80ms 폴러) 대신 WM_TIMER로 분리. Overlay.Initialize가
        //     동일한 초기값을 _capsLockOn에 주입하므로 첫 틱에 중복 UpdateColor가 발생하지 않는다.
        _lastCapsLockState = (User32.GetKeyState(Win32Constants.VK_CAPITAL) & 1) != 0;
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_CAPS, DefaultConfig.CapsLockPollMs, IntPtr.Zero);

        // 10. 감지 스레드 시작
        StartDetectionThread();

        // 11. IME 이벤트 훅 등록
        ImeStatus.RegisterHook(_hwndMain);

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
                HideOverlay();
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
                else
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
                {
                    // DragModifier=None: 기존 동작 (항상 드래그 가능, 모든 클릭 소비).
                    // 모디파이어 설정 시 해당 키가 눌려 있을 때만 HTCAPTION 반환 → 드래그 시작.
                    // 안 눌렸으면 HTTRANSPARENT 반환 → 클릭/휠이 아래 창으로 투과.
                    return IsDragModifierPressed(_config.DragModifier)
                        ? Win32Constants.HTCAPTION
                        : Win32Constants.HTTRANSPARENT;
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
    /// 현재 드래그 활성 키 설정에 해당하는 모디파이어가 눌려 있는지 확인.
    /// None 모드는 항상 true — 기존 동작(항상 드래그 가능) 유지.
    /// WM_NCHITTEST 메인 스레드에서 호출 — GetAsyncKeyState 스레드 제약 없음.
    /// </summary>
    private static bool IsDragModifierPressed(DragModifier mode)
    {
        const short KeyPressedMask = unchecked((short)0x8000);
        if (mode == DragModifier.None) return true;

        bool ctrl = (User32.GetAsyncKeyState(Win32Constants.VK_CONTROL) & KeyPressedMask) != 0;
        bool alt  = (User32.GetAsyncKeyState(Win32Constants.VK_MENU) & KeyPressedMask) != 0;
        return mode switch
        {
            DragModifier.Ctrl    => ctrl && !alt,
            DragModifier.Alt     => alt && !ctrl,
            DragModifier.CtrlAlt => ctrl && alt,
            _                    => true,
        };
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
            Overlay.Show(x, y, _lastImeState);
            return;
        }

        if (_config.PositionMode == PositionMode.Window)
        {
            // 창 기준 모드: 절대좌표 → 창 기준 상대 오프셋으로 변환 후 저장
            if (_currentProcessName.Length > 0)
            {
                RelativePositionConfig? rel =
                    Overlay.ComputeRelativeFromCurrentPosition(_lastForegroundHwnd);
                if (rel is not null)
                {
                    var positions = new Dictionary<string, int[]>(
                        _config.IndicatorPositionsRelative)
                    {
                        [_currentProcessName] = [(int)rel.Corner, rel.DeltaX, rel.DeltaY]
                    };
                    _config = _config with { IndicatorPositionsRelative = positions };
                    Settings.Save(_config);
                    Logger.Debug($"Saved relative position for {_currentProcessName}: "
                        + $"corner={rel.Corner}, delta=({rel.DeltaX}, {rel.DeltaY})");
                }
            }
        }
        else
        {
            // 고정 모드: 기존 절대좌표 저장
            if (_lastForegroundHwnd != IntPtr.Zero)
                _hwndPositions[_lastForegroundHwnd] = (x, y);
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
        }
        // 새 위치의 모니터 DPI로 리소스 재생성
        Overlay.Show(x, y, _lastImeState);
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
            var (x, y) = Overlay.ResolveRelativePosition(frame, relConfig);
            return ClampToVisibleArea(x, y);
        }
        // 2. 기본 상대 위치
        return Overlay.GetDefaultRelativePosition(
            _lastForegroundHwnd, _currentProcessName,
            _config.DefaultIndicatorPositionRelative);
    }

    /// <summary>
    /// 저장된 인디케이터 좌표를 현재 살아있는 모니터의 작업 영역 안으로 클램프.
    /// 모니터 제거 / 해상도 변경 / DPI 변경 후 저장 위치가 화면 밖이 될 수 있는 문제를 방어.
    /// 저장 값 자체는 덮어쓰지 않아서 원 모니터 복귀 시 원 위치가 복원된다.
    /// </summary>
    private static (int x, int y) ClampToVisibleArea(int x, int y)
    {
        var (w, h) = Overlay.GetBaseSize();
        if (w <= 0 || h <= 0) return (x, y);  // 엔진 아직 초기화 전

        // 인디 중심점 기준 가장 가까운 살아있는 모니터로 라우팅 (DEFAULTTONEAREST).
        // 저장 좌표가 제거된 모니터에 있었다면 잔존 모니터 중 가장 가까운 쪽으로 재매핑된다.
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x + w / 2, y + h / 2);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);

        // 인디 bbox 가 작업 영역 폭/높이를 초과하면 Left/Top 으로 고정 (Math.Clamp 역방향 방어).
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
        _config = Settings.Load();
        Logger.SetLevel(_config.LogLevel);
        Logger.Initialize(_config.LogToFile, _config.LogFilePath, _config.LogMaxSizeMb);
        I18n.Load(_config.Language);
        Settings.ClearProfileCache();
        Overlay.HandleConfigChanged(_config);
        // 인디가 가시 상태라면 애니메이터 config 갱신 + 새 alpha/크기/색상 즉시 반영
        if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
        {
            var (x, y) = GetAppPosition();
            Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
        }
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
    /// 메뉴 상단에 "새 버전 있음 ({version}) — 다운로드" 항목을 추가한다.
    /// </summary>
    private static void HandleUpdateFound()
    {
        var info = _pendingUpdate;
        if (info is null) return;
        Tray.OnUpdateFound(info);
    }

    /// <summary>
    /// 중복 실행된 두 번째 인스턴스의 WM_APP_ACTIVATE 수신 핸들러.
    /// 현재 포그라운드 앱 기준으로 인디케이터를 즉시 표시해 "이미 실행 중" 이라는 시각 피드백을 준다.
    /// DisplayMode / EventTriggers 설정과 무관하게 강제 표시 — 사용자의 명시적 재실행 행위에 대한 응답.
    /// </summary>
    private static void HandleActivateRequest()
    {
        Logger.Info("Activation request from second instance received");
        if (_lastForegroundHwnd == IntPtr.Zero) return;

        _indicatorVisible = true;
        var (x, y) = GetAppPosition();
        Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
    }

    /// <summary>
    /// Explorer 재시작(업데이트, 크래시 복구) 시 셸이 브로드캐스트하는 TaskbarCreated 메시지 핸들러.
    /// 셸은 재시작 시 모든 트레이 아이콘 등록 정보를 잃으므로 앱이 스스로 재등록해야 한다.
    /// </summary>
    private static void HandleTaskbarCreated()
    {
        Logger.Info("TaskbarCreated broadcast received, recreating tray icon");
        if (_config.TrayEnabled)
            Tray.Recreate(_lastImeState, _config);
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

    /// <summary>
    /// 메인 스레드 WM_TIMER(TIMER_ID_CAPS) 핸들러 — CAPS LOCK 토글 상태 폴링.
    /// 토글 비트만 변경됐을 때 Overlay._capsLockOn 필드를 갱신하고 인디가 가시 상태면 즉시 재렌더.
    /// 인디가 숨겨져 있으면 필드만 갱신하고 재렌더는 다음 표시 시점으로 지연된다.
    /// </summary>
    private static void HandleCapsLockTimer()
    {
        bool current = (User32.GetKeyState(Win32Constants.VK_CAPITAL) & 1) != 0;
        if (current == _lastCapsLockState) return;

        _lastCapsLockState = current;
        Logger.Debug($"CapsLock: {(current ? "ON" : "OFF")}");
        Overlay.SetCapsLock(current);
        if (_indicatorVisible)
            Overlay.UpdateColor(_lastImeState);
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
        Tray.HandleMenuCommand(commandId, _config, _hwndMain, _lastForegroundHwnd,
            updateConfig: newConfig =>
            {
                _config = ThemePresets.Apply(newConfig);
                Overlay.HandleConfigChanged(_config);
                // 인디가 가시 상태라면 애니메이터 config 갱신 + 새 alpha/크기/색상이 즉시 반영되도록
                // TriggerShow로 전체 갱신. TriggerShow는 UpdateConfig + Overlay.Show를 포함한다.
                if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
                {
                    var (x, y) = GetAppPosition();
                    Animation.TriggerShow(x, y, _lastImeState, _config, imeChanged: false);
                }
                else if (_indicatorVisible)
                {
                    Overlay.UpdateColor(_lastImeState);
                }
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
        Overlay.HandleDpiChanged();
    }

    private static void HandleDisplayChange()
    {
        Logger.Info("Display changed");
        Overlay.HandleDpiChanged();

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
        Overlay.HandleDpiChanged();
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
        RECT lastWindowFrame = default;        // 창 기준 모드: 포그라운드 창 rect 추적
        bool windowMoving = false;             // 창 기준 모드: 창 이동 중 → 인디 숨김
        bool lastFiltered = false;
        ImeState lastImeState = ImeState.English;
        AppConfig lastAppConfig = _config;
        int pollCount = 0;

        while (!_stopping)
        {
            Thread.Sleep(_config.PollIntervalMs);
            try
            {
                pollCount++;

                // 0. config.json 변경 감지 (~5초마다)
                if (pollCount % DefaultConfig.ConfigCheckIntervalPolls == 0)
                    Settings.CheckConfigFileChange(_hwndMain);

                // 1. 포그라운드 윈도우 확인
                IntPtr hwndForeground = User32.GetForegroundWindow();

                // 자기 자신 무시
                if (hwndForeground == _hwndMain || hwndForeground == _hwndOverlay)
                    continue;

                // 모달 게이트용 PID 와 GUITHREADINFO 용 threadId 를 한 번에 확보.
                uint threadId = User32.GetWindowThreadProcessId(hwndForeground, out uint fgPid);

                // 모달 대화상자 활성 + 포그라운드가 자기 프로세스일 때만 인디를 숨긴다.
                // Win32 다이얼로그는 소유자 기준 모달일 뿐이라 Alt+Tab 으로 외부 앱에 포커스가
                // 넘어가면 해당 앱에서는 인디가 정상 표시돼야 한다. PID 비교는 자체 대화상자 +
                // MessageBoxW (HWND 가 user32 내부 소유라 ActiveDialog HWND 로 식별 불가) 를
                // 모두 커버하는 유일한 견고 해법 — Environment.ProcessId 는 .NET BCL 속성이라
                // P/Invoke 불필요. lastFiltered=true 로 모달 종료 후 원 앱 foreground 복귀 첫
                // 틱에서 foregroundChanged=true 를 유도 → 자연 재표시.
                if (ModalDialogLoop.IsActive && fgPid == (uint)Environment.ProcessId)
                {
                    if (!lastFiltered && _indicatorVisible)
                        User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                            IntPtr.Zero, IntPtr.Zero);
                    lastFiltered = true;
                    continue;
                }

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
                // leavingSystemInput: 시스템 입력 프로세스(검색 창 등)에서 일반 앱으로 즉시
                // 전환되는 경우를 감지하기 위해 갱신 전 이전 프로세스명을 확인한다.
                bool leavingSystemInput = false;
                if (hwndForeground != lastHwndForeground)
                {
                    leavingSystemInput = DefaultConfig.IsSystemInputProcess(lastForegroundProcessName);
                    lastForegroundProcessName = WindowProcessInfo.GetProcessName(hwndForeground);
                    lastSystemInputFrame = default;
                    lastWindowFrame = default;
                    windowMoving = false;
                }

                // 필터 해소 또는 포그라운드 변경 → 위치/포커스 갱신
                bool foregroundChanged = (hwndForeground != lastHwndForeground) || lastFiltered;
                lastFiltered = false;

                // ── 시스템 입력 프로세스 닫힘 감지 ──
                //
                // 시작 메뉴(StartMenuExperienceHost)와 검색 창(SearchHost)은 SystemFilter 블랙리스트에
                // 없어 인디케이터를 표시하지만, ESC로 닫힌 뒤에도 숨김 전환이 발생하지 않는 문제가 있다.
                // 두 프로세스의 ESC 후 동작이 다르므로 두 가지 체크가 필요하다:
                //
                // (A) SMEH: ESC 후 foreground를 유지한 채 DWM cloaked 상태가 됨 (수 초간 지속).
                //     IsWindowVisible=true, hwndFocus≠0이라 ShouldHide 8조건을 모두 통과한다.
                //     → IsCloaked로 감지하여 숨김.
                //
                // (B) SearchHost: ESC 후 cloaked 없이 foreground가 즉시 다른 앱으로 변경됨.
                //     non-filtered→non-filtered 전환이라 기존 숨김 로직이 동작하지 않음.
                //     → leavingSystemInput 플래그로 감지하여 숨김.

                // (A) HWND 유지 + cloaked: 시작 메뉴 ESC 후 foreground가 아직 안 바뀐 경우
                if (!leavingSystemInput
                    && DefaultConfig.IsSystemInputProcess(lastForegroundProcessName)
                    && Dwmapi.IsCloaked(hwndForeground))
                {
                    if (_indicatorVisible)
                        User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                            IntPtr.Zero, IntPtr.Zero);
                    lastHwndForeground = hwndForeground;
                    lastHwndFocus = hwndFocus;
                    lastSystemInputFrame = default;
                    continue;
                }

                // (B) 즉시 전환: 검색 창 등에서 일반 앱으로 직접 변경된 경우
                //     시스템 입력 간 전환(시작 메뉴 ↔ 검색)은 제외.
                //     인디가 이미 숨겨진 경우(A에 의해)에는 fall-through하여 새 앱에 즉시 표시.
                if (leavingSystemInput
                    && !DefaultConfig.IsSystemInputProcess(lastForegroundProcessName))
                {
                    if (_indicatorVisible)
                    {
                        User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                            IntPtr.Zero, IntPtr.Zero);
                        // lastHwndForeground 미갱신 → 다음 틱에서 foreground 변경 감지 → 새 앱에 인디 표시
                        lastHwndFocus = hwndFocus;
                        lastSystemInputFrame = default;
                        continue;
                    }
                }

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

                // 창 기준 모드: 포그라운드 창 rect 변화 감지 → 이동 중 인디 숨김, 안정화 시 재표시.
                // 시스템 입력 프로세스는 위의 전용 블록에서 처리하므로 제외.
                if (_config.PositionMode == PositionMode.Window
                    && !DefaultConfig.IsSystemInputProcess(lastForegroundProcessName)
                    && !foregroundChanged
                    && Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT windowFrame))
                {
                    bool rectChanged = windowFrame.Left != lastWindowFrame.Left
                        || windowFrame.Top != lastWindowFrame.Top
                        || windowFrame.Right != lastWindowFrame.Right
                        || windowFrame.Bottom != lastWindowFrame.Bottom;

                    if (rectChanged)
                    {
                        if (_indicatorVisible && !windowMoving)
                            User32.PostMessageW(_hwndMain, AppMessages.WM_HIDE_INDICATOR,
                                IntPtr.Zero, IntPtr.Zero);
                        windowMoving = true;
                        lastWindowFrame = windowFrame;
                    }
                    else if (windowMoving)
                    {
                        // 창 이동 멈춤 → 새 위치에서 인디 재표시
                        windowMoving = false;
                        foregroundChanged = true;
                    }
                }

                // 3. 포그라운드 변경 시 위치 갱신
                if (foregroundChanged)
                {
                    User32.PostMessageW(_hwndMain, AppMessages.WM_POSITION_UPDATED,
                        hwndForeground, IntPtr.Zero);
                    lastHwndForeground = hwndForeground;
                }

                // 4. IME 상태 감지
                ImeState currentIme = ImeStatus.Detect(hwndFocus, threadId, appConfig.DetectionMethod);
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
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or COMException
                or ArgumentException)
            {
                // 감지 루프 본문은 P/Invoke(User32/Dwmapi/Imm32) + VDM COM + Process.GetProcessById
                // 조합이므로 일시적 Win32/COM/프로세스 실패는 흡수하고 다음 폴링에서 재개한다.
                // 로직 버그(NullRef 등)는 전파되어 감지 스레드 종료로 드러난다.
                Logger.Warning($"Detection loop error: {ex.Message}");
            }
        }
    }

}
