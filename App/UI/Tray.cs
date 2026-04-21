using System.Diagnostics;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI.Dialogs;
using KoEnVue.App.Update;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Tray;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI;

/// <summary>
/// Shell_NotifyIconW 기반 시스템 트레이 아이콘 관리 + 팝업 메뉴 + 시작등록 + 설정파일 열기.
/// WinForms NotifyIcon 사용 금지 (P1).
/// </summary>
internal static class Tray
{
    // ================================================================
    // 메뉴 항목 ID (P3: 매직 넘버 금지)
    // ================================================================

    // 서브메뉴: 투명도
    private const int IDM_OPACITY_HIGH    = 3001;
    private const int IDM_OPACITY_NORMAL  = 3002;
    private const int IDM_OPACITY_LOW     = 3003;

    // 서브메뉴: 기본 위치
    private const int IDM_DEFAULT_POS_SET_CURRENT = 3101;
    private const int IDM_DEFAULT_POS_RESET       = 3102;

    // 서브메뉴: 크기 배율
    // 정수 프리셋 — Nx → IDM_SIZE_BASE + N - ScaleIntegerMin. N ∈ [ScaleIntegerMin, ScaleIntegerMax].
    // IDM_SIZE_CUSTOM — "직접 지정" 대화상자 호출. 범위/허용오차는 ScaleInputDialog에 정의.
    private const int IDM_SIZE_BASE = 3201;
    private const int IDM_SIZE_CUSTOM = 3206;
    private const int ScaleIntegerMin = 1;
    private const int ScaleIntegerMax = 5;

    // 서브메뉴: 위치 모드
    private const int IDM_POSITION_FIXED  = 3301;
    private const int IDM_POSITION_WINDOW = 3302;

    // 서브메뉴: 드래그 활성 키
    private const int IDM_DRAG_MOD_NONE     = 3401;
    private const int IDM_DRAG_MOD_CTRL     = 3402;
    private const int IDM_DRAG_MOD_ALT      = 3403;
    private const int IDM_DRAG_MOD_CTRL_ALT = 3404;

    // 메인 메뉴
    private const int IDM_STARTUP            = 4001;
    private const int IDM_CLEANUP            = 4003;
    private const int IDM_SNAP_TO_WINDOWS    = 4004;
    private const int IDM_SETTINGS           = 4005;
    private const int IDM_ANIMATION_ENABLED  = 4006;
    private const int IDM_CHANGE_HIGHLIGHT   = 4007;
    private const int IDM_UPDATE_DOWNLOAD    = 4008;
    private const int IDM_USER_HIDDEN        = 4009;
    private const int IDM_EXIT               = 4002;

    // schtasks 작업 이름
    private const string TaskName = "KoEnVue";

    // 시작 프로그램 로그온 지연 (ISO 8601 duration).
    // 부팅 자동 실행 시 explorer 트레이 초기화 전에 앱이 떠서 Shell_NotifyIconW NIM_ADD 가 실패하는
    // 레이스를 회피한다. 재시도로 복구되긴 하지만 매 부팅마다 warn 로그가 남는 문제 해소 목적.
    private const string StartupTaskDelay = "PT15S";

    // P3: 매직 넘버 금지
    private const double OpacityTolerance = 0.001;
    private const int SchtasksQueryTimeoutMs = 3000;
    private const int SchtasksCommandTimeoutMs = 5000;

    // NIM_ADD 재시도 — startup 레이스 대비 (explorer 트레이 초기화 전에 task 가 먼저 기동).
    // 1s 간격 × 30회 = 최대 30초 대기. 그 안에 explorer 가 안 떠 있으면 포기.
    private const uint TrayAddRetryIntervalMs = 1000;
    private const int TrayAddRetryMaxAttempts = 30;

    // ================================================================
    // 내부 상태
    // ================================================================

    private static bool _initialized;
    private static IntPtr _hwndMain;
    private static SafeIconHandle? _currentIcon;
    private static NotifyIconManager? _notifyIcon;

    // NIM_ADD 재시도 상태. 첫 Add 실패 시 WM_TIMER 로 폴백 → HandleTrayAddRetry 가 소비.
    private static bool _addPending;
    private static int _addRetryCount;
    private static ImeState _pendingInitialState;
    private static AppConfig? _pendingConfig;

    // UpdateChecker 가 발견한 새 버전 정보. null 이면 메뉴에 업데이트 항목 미표시.
    // 메인 스레드 전용 (Program.HandleUpdateFound → OnUpdateFound 경로) 이라 volatile 불필요.
    private static UpdateInfo? _pendingUpdate;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 트레이 아이콘 등록 (NIM_ADD + NIM_SETVERSION).
    /// config.TrayEnabled == false 이면 건너뛴다.
    /// NIM_ADD 실패 시 WM_TIMER 로 1초 간격 재시도 — 부팅 레이스(explorer 의 트레이 초기화
    /// 전에 scheduled task 로 기동된 경우) 대비. TaskbarCreated 브로드캐스트를 못 받는
    /// 환경(예: 재시작 없이 느리게 준비된 explorer) 에서도 복구 가능.
    /// </summary>
    internal static void Initialize(IntPtr hwndMain, ImeState initialState, AppConfig config)
    {
        _hwndMain = hwndMain;

        if (!config.TrayEnabled)
        {
            _initialized = false;
            Logger.Debug("Tray disabled by config");
            return;
        }

        _currentIcon = TrayIcon.CreateIcon(initialState, config);

        _notifyIcon = new NotifyIconManager(hwndMain, AppMessages.WM_TRAY_CALLBACK, DefaultConfig.AppGuid);
        bool added = _notifyIcon.Add(_currentIcon.DangerousGetHandle(), BuildTooltip(initialState, config));

        _initialized = true;

        if (!added)
        {
            _addPending = true;
            _addRetryCount = 0;
            _pendingInitialState = initialState;
            _pendingConfig = config;
            User32.SetTimer(hwndMain, AppMessages.TIMER_ID_TRAY_ADD_RETRY,
                TrayAddRetryIntervalMs, IntPtr.Zero);
            Logger.Warning("Tray icon NIM_ADD failed; retry timer scheduled");
        }
        else
        {
            Logger.Info("Tray icon initialized");
        }
    }

    /// <summary>
    /// WM_TIMER(TIMER_ID_TRAY_ADD_RETRY) 핸들러. 첫 NIM_ADD 가 실패한 경우에 한해
    /// 1초 간격으로 재시도한다. 성공하거나 최대 시도 횟수에 도달하면 타이머 해제.
    /// TaskbarCreated 브로드캐스트가 선행 도착하면 Recreate 경로에서 _addPending 을
    /// 정리하므로 본 타이머도 자연스럽게 stop 된다.
    /// </summary>
    internal static void HandleAddRetryTimer()
    {
        if (!_addPending || _notifyIcon is null || _currentIcon is null || _pendingConfig is null)
        {
            StopAddRetryTimer();
            return;
        }

        _addRetryCount++;
        // _pendingConfig 는 Initialize 실패 경로에서 반드시 설정됨 — null 이면 위의 가드에
        // 걸려 이 지점에 도달하지 않는다.
        bool added = _notifyIcon.Add(_currentIcon.DangerousGetHandle(),
            BuildTooltip(_pendingInitialState, _pendingConfig!));

        if (added)
        {
            Logger.Info($"Tray icon NIM_ADD recovered after {_addRetryCount} retry(s)");
            StopAddRetryTimer();
        }
        else if (_addRetryCount >= TrayAddRetryMaxAttempts)
        {
            Logger.Warning($"Tray icon NIM_ADD gave up after {_addRetryCount} retries");
            StopAddRetryTimer();
        }
    }

    private static void StopAddRetryTimer()
    {
        if (_hwndMain != IntPtr.Zero)
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_TRAY_ADD_RETRY);
        _addPending = false;
        _pendingConfig = null;
    }

    /// <summary>
    /// IME 상태 변경 또는 config 변경 시 아이콘 + 툴팁 갱신 (NIM_MODIFY).
    /// </summary>
    internal static void UpdateState(ImeState state, AppConfig config)
    {
        if (!_initialized) return;

        var newIcon = TrayIcon.CreateIcon(state, config);
        _notifyIcon?.UpdateIconAndTooltip(newIcon.DangerousGetHandle(), BuildTooltip(state, config));

        // 이전 아이콘 해제 후 교체 — 소유권은 Tray.cs 측에 남는다 (NotifyIconManager 는 해제 금지).
        _currentIcon?.Dispose();
        _currentIcon = newIcon;

        // Add 재시도 중이면 pending 상태도 최신화 — 재시도 성공 시 툴팁이 오래된 초기 상태로
        // 남는 걸 방지. 아이콘 자체는 _currentIcon 참조로 이미 최신이라 별도 처리 불필요.
        if (_addPending)
        {
            _pendingInitialState = state;
            _pendingConfig = config;
        }
    }

    /// <summary>
    /// UpdateChecker 가 새 버전을 발견했을 때 Program.HandleUpdateFound 가 호출.
    /// 페이로드를 보관만 하고, 다음 메뉴 빌드 시점에 ShowMenu 가 자동으로 항목을 노출한다.
    /// </summary>
    internal static void OnUpdateFound(UpdateInfo info)
    {
        _pendingUpdate = info;
        Logger.Info($"Tray: update available — {info.Version} ({info.HtmlUrl})");
    }

    /// <summary>
    /// 트레이 아이콘 재등록.
    /// Explorer 재시작(업데이트, 크래시) 또는 다른 원인으로 셸이 아이콘을 잃었을 때
    /// TaskbarCreated 브로드캐스트를 수신한 Program 이 호출.
    /// 내부 상태를 초기화한 뒤 Initialize 를 다시 호출한다 — 셸 측에 이전 등록이 없으므로
    /// NIM_DELETE 는 실패해도 무해하다.
    /// </summary>
    internal static void Recreate(ImeState state, AppConfig config)
    {
        // TaskbarCreated 가 Initialize 이전에 도착하는 레이스(트레이 브로드캐스트 → Program 수신 선행)
        // 에서는 _hwndMain 만 세팅된 상태일 수 있다. _initialized 까지 확인해 Remove 가 NIM_DELETE
        // 없이 내부 상태만 초기화하는 경로를 타도록 가드 — 초기화 절반만 이뤄진 채 Initialize 가
        // 재호출되면 _currentIcon 참조가 유실돼 핸들 누수로 이어진다.
        if (_hwndMain == IntPtr.Zero || !_initialized) return;

        IntPtr hwndMain = _hwndMain;
        Remove();
        Initialize(hwndMain, state, config);
        Logger.Info("Tray icon recreated (TaskbarCreated or recovery)");
    }

    /// <summary>
    /// 트레이 아이콘 제거 (NIM_DELETE). 앱 종료 시 호출.
    /// </summary>
    internal static void Remove()
    {
        if (!_initialized) return;

        // 재시도 타이머가 남아 있으면 먼저 정리 — Remove 후 Initialize 가 재호출되는 Recreate
        // 경로에서 이전 retry 상태가 새 초기화에 섞이지 않도록.
        StopAddRetryTimer();

        bool removed = _notifyIcon?.Remove() ?? true;
        if (!removed)
            Logger.Warning("Failed to remove tray icon on shutdown");

        _currentIcon?.Dispose();
        _currentIcon = null;
        _notifyIcon = null;
        _initialized = false;

        Logger.Info("Tray icon removed");
    }

    /// <summary>
    /// 트레이 우클릭 팝업 메뉴 표시.
    /// </summary>
    internal static void ShowMenu(IntPtr hwndMain, AppConfig config)
    {
        if (!_initialized) return;

        // --- 서브메뉴: 투명도 ---
        IntPtr hOpacityMenu = User32.CreatePopupMenu();
        double[] presets = config.TrayQuickOpacityPresets;
        if (presets.Length >= 3)
        {
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_HIGH,
                $"{I18n.OpacityHigh} {presets[0]}");
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_NORMAL,
                $"{I18n.OpacityNormal} {presets[1]}");
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_LOW,
                $"{I18n.OpacityLow} {presets[2]}");

            // 현재 opacity와 매칭되는 프리셋에 라디오 체크
            // Always 모드에서는 ActiveOpacity가 실제 적용 값이므로 이를 기준으로 비교
            double effectiveOpacity = config.DisplayMode == DisplayMode.Always
                ? config.ActiveOpacity
                : config.Opacity;
            uint opacityCheckId = 0;
            if (Math.Abs(effectiveOpacity - presets[0]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_HIGH;
            else if (Math.Abs(effectiveOpacity - presets[1]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_NORMAL;
            else if (Math.Abs(effectiveOpacity - presets[2]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_LOW;

            if (opacityCheckId != 0)
                User32.CheckMenuRadioItem(hOpacityMenu, (uint)IDM_OPACITY_HIGH, (uint)IDM_OPACITY_LOW,
                    opacityCheckId, Win32Constants.MF_BYCOMMAND);
        }

        // --- 서브메뉴: 크기 배율 ---
        // 정수 프리셋 5개 + 직접 지정(대화상자). 현재 배율이 비정수면
        // "직접 지정 (2.3배)" 형태로 값을 라벨에 노출하고 해당 항목에 라디오 체크.
        IntPtr hSizeMenu = User32.CreatePopupMenu();
        for (int n = ScaleIntegerMin; n <= ScaleIntegerMax; n++)
        {
            User32.AppendMenuW(hSizeMenu, Win32Constants.MF_STRING,
                (nuint)(IDM_SIZE_BASE + n - ScaleIntegerMin), I18n.GetSizeLabel(n));
        }

        double currentScale = Math.Clamp(config.IndicatorScale,
            ScaleInputDialog.ScaleMinValue, ScaleInputDialog.ScaleMaxValue);
        bool isIntegerScale = ScaleInputDialog.IsIntegerScale(currentScale);
        string customLabel = isIntegerScale
            ? I18n.MenuSizeCustom
            : I18n.FormatCustomScaleLabel(currentScale);
        User32.AppendMenuW(hSizeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_SIZE_CUSTOM, customLabel);

        uint sizeCheckId;
        if (isIntegerScale)
        {
            int intScale = Math.Clamp((int)Math.Round(currentScale), ScaleIntegerMin, ScaleIntegerMax);
            sizeCheckId = (uint)(IDM_SIZE_BASE + intScale - ScaleIntegerMin);
        }
        else
        {
            sizeCheckId = (uint)IDM_SIZE_CUSTOM;
        }
        User32.CheckMenuRadioItem(hSizeMenu,
            (uint)IDM_SIZE_BASE,
            (uint)IDM_SIZE_CUSTOM,
            sizeCheckId,
            Win32Constants.MF_BYCOMMAND);

        // --- 서브메뉴: 기본 위치 ---
        IntPtr hDefaultPosMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hDefaultPosMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DEFAULT_POS_SET_CURRENT, I18n.MenuDefaultPosSetCurrent);
        bool hasDefault = config.PositionMode == PositionMode.Window
            ? config.DefaultIndicatorPositionRelative is not null
            : config.DefaultIndicatorPosition is not null;
        uint resetFlags = Win32Constants.MF_STRING
            | (hasDefault ? 0u : Win32Constants.MF_GRAYED);
        User32.AppendMenuW(hDefaultPosMenu, resetFlags,
            (nuint)IDM_DEFAULT_POS_RESET, I18n.MenuDefaultPosReset);

        // --- 서브메뉴: 위치 모드 ---
        IntPtr hPositionModeMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hPositionModeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_POSITION_FIXED, I18n.MenuPositionFixed);
        User32.AppendMenuW(hPositionModeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_POSITION_WINDOW, I18n.MenuPositionWindow);
        uint positionModeCheckId = config.PositionMode == PositionMode.Fixed
            ? (uint)IDM_POSITION_FIXED : (uint)IDM_POSITION_WINDOW;
        User32.CheckMenuRadioItem(hPositionModeMenu, (uint)IDM_POSITION_FIXED,
            (uint)IDM_POSITION_WINDOW, positionModeCheckId, Win32Constants.MF_BYCOMMAND);

        // --- 서브메뉴: 드래그 활성 키 ---
        IntPtr hDragModMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_NONE, I18n.MenuDragModifierNone);
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_CTRL, I18n.MenuDragModifierCtrl);
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_ALT, I18n.MenuDragModifierAlt);
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_CTRL_ALT, I18n.MenuDragModifierCtrlAlt);
        uint dragModCheckId = config.DragModifier switch
        {
            DragModifier.Ctrl    => (uint)IDM_DRAG_MOD_CTRL,
            DragModifier.Alt     => (uint)IDM_DRAG_MOD_ALT,
            DragModifier.CtrlAlt => (uint)IDM_DRAG_MOD_CTRL_ALT,
            _                    => (uint)IDM_DRAG_MOD_NONE,
        };
        User32.CheckMenuRadioItem(hDragModMenu, (uint)IDM_DRAG_MOD_NONE,
            (uint)IDM_DRAG_MOD_CTRL_ALT, dragModCheckId, Win32Constants.MF_BYCOMMAND);

        // --- 메인 메뉴 ---
        IntPtr hMenu = User32.CreatePopupMenu();

        // 업데이트 알림 항목 — UpdateChecker 가 새 버전을 발견했을 때만 메뉴 최상단에 표시.
        // 클릭 시 GitHub 릴리스 페이지가 기본 브라우저에서 열린다.
        if (_pendingUpdate is not null)
        {
            User32.AppendMenuW(hMenu, Win32Constants.MF_STRING,
                (nuint)IDM_UPDATE_DOWNLOAD,
                I18n.FormatMenuUpdateAvailable(_pendingUpdate.Version));
            User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        }

        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hOpacityMenu, I18n.MenuOpacity);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hSizeMenu, I18n.MenuSize);
        uint snapFlags = config.SnapToWindows ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, snapFlags, (nuint)IDM_SNAP_TO_WINDOWS, I18n.MenuSnapToWindows);
        uint animationFlags = config.AnimationEnabled ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, animationFlags, (nuint)IDM_ANIMATION_ENABLED, I18n.MenuAnimation);
        uint highlightFlags = config.ChangeHighlight ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, highlightFlags, (nuint)IDM_CHANGE_HIGHLIGHT, I18n.MenuChangeHighlight);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);

        bool isStartup = IsStartupRegistered();
        User32.AppendMenuW(hMenu, isStartup ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED,
            (nuint)IDM_STARTUP, I18n.MenuStartup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hDefaultPosMenu, I18n.MenuDefaultPosition);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hPositionModeMenu, I18n.MenuPositionMode);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hDragModMenu, I18n.MenuDragModifier);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_CLEANUP, I18n.MenuCleanup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        uint userHiddenFlags = config.UserHidden ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, userHiddenFlags, (nuint)IDM_USER_HIDDEN, I18n.MenuUserHidden);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_SETTINGS, I18n.MenuSettings);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_EXIT, I18n.MenuExit);

        // --- 표시 (워크어라운드 적용) ---
        User32.GetCursorPos(out POINT pt);
        User32.SetForegroundWindow(hwndMain);
        User32.TrackPopupMenu(hMenu, Win32Constants.TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, hwndMain, IntPtr.Zero);
        User32.PostMessageW(hwndMain, Win32Constants.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        // --- 정리 (DestroyMenu 은 부모에 MF_POPUP 로 부착된 서브메뉴만 자동 파괴한다) ---
        // 위의 AppendMenuW(MF_POPUP) 호출이 모두 성공해 부착된 전제하에 안전.
        // P/Invoke 는 예외를 던지지 않고 BOOL 로 실패 신호만 주므로 중단 경로가 없어 현 구현은 누수가 없다.
        User32.DestroyMenu(hMenu);
    }

    /// <summary>
    /// WM_COMMAND 메뉴 명령 처리.
    /// config 변경이 필요한 항목은 updateConfig 콜백으로 Program.cs에 위임.
    /// </summary>
    internal static void HandleMenuCommand(int commandId, AppConfig config, IntPtr hwndMain,
        IntPtr hwndForeground, Action<AppConfig> updateConfig)
    {
        // --- 크기 배율 정수 프리셋 (동적 ID 범위 매칭) ---
        if (commandId >= IDM_SIZE_BASE && commandId < IDM_SIZE_BASE + (ScaleIntegerMax - ScaleIntegerMin + 1))
        {
            double newScale = ScaleIntegerMin + (commandId - IDM_SIZE_BASE);
            if (Math.Abs(newScale - config.IndicatorScale) > ScaleInputDialog.ScaleTolerance)
                updateConfig(config with { IndicatorScale = newScale });
            return;
        }

        // --- 크기 배율 직접 지정 대화상자 ---
        if (commandId == IDM_SIZE_CUSTOM)
        {
            double? typed = ScaleInputDialog.Show(_hwndMain, config.IndicatorScale);
            if (typed.HasValue)
            {
                double rounded = Math.Round(typed.Value, 1);
                if (Math.Abs(rounded - config.IndicatorScale) > ScaleInputDialog.ScaleTolerance)
                    updateConfig(config with { IndicatorScale = rounded });
            }
            return;
        }

        switch (commandId)
        {
            // --- 투명도 ---
            case IDM_OPACITY_HIGH:
                if (config.TrayQuickOpacityPresets.Length >= 1)
                    updateConfig(ApplyQuickOpacity(config, config.TrayQuickOpacityPresets[0]));
                break;
            case IDM_OPACITY_NORMAL:
                if (config.TrayQuickOpacityPresets.Length >= 2)
                    updateConfig(ApplyQuickOpacity(config, config.TrayQuickOpacityPresets[1]));
                break;
            case IDM_OPACITY_LOW:
                if (config.TrayQuickOpacityPresets.Length >= 3)
                    updateConfig(ApplyQuickOpacity(config, config.TrayQuickOpacityPresets[2]));
                break;

            // --- 시작 프로그램 등록 ---
            case IDM_STARTUP:
                ToggleStartupRegistration();
                break;

            // --- 기본 위치: 현재 위치로 설정 ---
            case IDM_DEFAULT_POS_SET_CURRENT:
                SetDefaultPositionToCurrent(config, hwndForeground, updateConfig);
                break;

            // --- 기본 위치: 초기화 ---
            case IDM_DEFAULT_POS_RESET:
                if (config.PositionMode == PositionMode.Window)
                    updateConfig(config with { DefaultIndicatorPositionRelative = null });
                else
                    updateConfig(config with { DefaultIndicatorPosition = null });
                Logger.Info("Default indicator position reset to hardcoded fallback");
                break;

            // --- 위치 모드: 고정 위치 ---
            case IDM_POSITION_FIXED:
                if (config.PositionMode != PositionMode.Fixed)
                {
                    updateConfig(config with { PositionMode = PositionMode.Fixed });
                    Logger.Info("Position mode changed to Fixed");
                }
                break;

            // --- 위치 모드: 창 기준 ---
            case IDM_POSITION_WINDOW:
                if (config.PositionMode != PositionMode.Window)
                {
                    updateConfig(config with { PositionMode = PositionMode.Window });
                    Logger.Info("Position mode changed to Window");
                }
                break;

            // --- 드래그 활성 키 ---
            case IDM_DRAG_MOD_NONE:
                if (config.DragModifier != DragModifier.None)
                {
                    updateConfig(config with { DragModifier = DragModifier.None });
                    Logger.Info("DragModifier changed to None");
                }
                break;
            case IDM_DRAG_MOD_CTRL:
                if (config.DragModifier != DragModifier.Ctrl)
                {
                    updateConfig(config with { DragModifier = DragModifier.Ctrl });
                    Logger.Info("DragModifier changed to Ctrl");
                }
                break;
            case IDM_DRAG_MOD_ALT:
                if (config.DragModifier != DragModifier.Alt)
                {
                    updateConfig(config with { DragModifier = DragModifier.Alt });
                    Logger.Info("DragModifier changed to Alt");
                }
                break;
            case IDM_DRAG_MOD_CTRL_ALT:
                if (config.DragModifier != DragModifier.CtrlAlt)
                {
                    updateConfig(config with { DragModifier = DragModifier.CtrlAlt });
                    Logger.Info("DragModifier changed to CtrlAlt");
                }
                break;

            // --- 창에 자석처럼 붙이기 토글 ---
            case IDM_SNAP_TO_WINDOWS:
                updateConfig(config with { SnapToWindows = !config.SnapToWindows });
                Logger.Info($"SnapToWindows toggled: {!config.SnapToWindows}");
                break;

            // --- 애니메이션 사용 토글 ---
            case IDM_ANIMATION_ENABLED:
                updateConfig(config with { AnimationEnabled = !config.AnimationEnabled });
                Logger.Info($"AnimationEnabled toggled: {!config.AnimationEnabled}");
                break;

            // --- 변경 시 강조 토글 ---
            case IDM_CHANGE_HIGHLIGHT:
                updateConfig(config with { ChangeHighlight = !config.ChangeHighlight });
                Logger.Info($"ChangeHighlight toggled: {!config.ChangeHighlight}");
                break;

            // --- 위치 기록 정리 ---
            case IDM_CLEANUP:
                CleanupPositions(config, updateConfig);
                break;

            // --- 인디케이터 숨김 토글 ---
            // 좌클릭 동작이 Settings/None 이라 좌클릭 토글이 막혀 있어도 숨김 해제 경로를 보장.
            case IDM_USER_HIDDEN:
                updateConfig(config with { UserHidden = !config.UserHidden });
                Logger.Info($"UserHidden toggled via menu: {!config.UserHidden}");
                break;

            // --- 상세 설정 ---
            case IDM_SETTINGS:
                SettingsDialog.Show(hwndMain, config, updateConfig);
                break;

            // --- 업데이트 다운로드 — GitHub 릴리스 페이지 오픈 ---
            case IDM_UPDATE_DOWNLOAD:
                OpenUpdatePage();
                break;

            // --- 종료 ---
            case IDM_EXIT:
                User32.PostQuitMessage(0);
                break;
        }
    }

    /// <summary>
    /// 펜딩된 업데이트의 GitHub 릴리스 페이지를 기본 브라우저로 연다.
    /// ShellExecuteW 반환값이 32 이하면 실패지만 사용자에게 알려도 할 일이 없으므로 silent log.
    /// <para>
    /// <b>URL 프리픽스 검증</b> — <c>info.HtmlUrl</c> 은 GitHub API 응답 JSON 의 <c>html_url</c> 필드에서 왔다.
    /// 신뢰된 CA 를 가진 MITM 프록시가 응답을 조작하거나 계정이 탈취되면 <c>file:///</c>·<c>javascript:</c>·
    /// <c>ms-settings:</c> 등의 스킴이 주입될 가능성이 있다. 앱이 <c>requireAdministrator</c> 로 기동되므로
    /// 임의 스킴 실행은 EoP 로 번질 수 있다. 따라서 예상 릴리스 페이지 URL 프리픽스
    /// (<c>https://github.com/{owner}/{name}/</c>) 와 일치하지 않으면 열지 않는다.
    /// </para>
    /// </summary>
    private static void OpenUpdatePage()
    {
        var info = _pendingUpdate;
        if (info is null)
        {
            Logger.Debug("OpenUpdatePage called with no pending update");
            return;
        }

        string expectedPrefix = $"https://github.com/{DefaultConfig.UpdateRepoOwner}/{DefaultConfig.UpdateRepoName}/";
        if (!info.HtmlUrl.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warning($"Refused to open update URL with unexpected prefix: {info.HtmlUrl}");
            return;
        }

        IntPtr result = Shell32.ShellExecuteW(
            IntPtr.Zero,
            "open",
            info.HtmlUrl,
            null,
            null,
            Win32Constants.SW_SHOWNORMAL);

        // ShellExecuteW 의 반환값은 HINSTANCE 이지만 의미상 정수: <= 32 이면 실패.
        if ((long)result <= 32)
            Logger.Warning($"ShellExecuteW failed for update URL (rc={(long)result})");
        else
            Logger.Info($"Opened update page: {info.HtmlUrl}");
    }

    /// <summary>
    /// 현재 활성 config.json 을 메모장으로 연다 (트레이 좌클릭 "설정 파일 열기" 동작).
    /// <para>
    /// <b>메모장 고정 이유</b> — 일반 사용자 PC 에는 <c>.json</c> 에 연결된 기본 앱이 없어
    /// <c>ShellExecuteW("open", "...json")</c> 가 "앱 선택" 다이얼로그를 띄우거나 무반응으로
    /// 보일 확률이 높다. 메모장은 Windows 모든 버전에 기본 탑재되고 UTF-8 을 정상 표시하며
    /// 저장 시 hot reload 파일 감시에 바로 감지된다.
    /// </para>
    /// <para>
    /// 경로에 공백이 있을 수 있어(<c>C:\Program Files\...</c>) <c>lpParameters</c> 를
    /// 따옴표로 감싼다. ShellExecuteW 반환값이 32 이하면 실패지만 사용자에게 알려도 할 일이
    /// 없으므로 silent log.
    /// </para>
    /// </summary>
    internal static void OpenConfigFile()
    {
        string? path = Settings.ConfigFilePath;
        if (string.IsNullOrEmpty(path))
        {
            Logger.Warning("OpenConfigFile: ConfigFilePath is null (Settings.Load not yet called)");
            return;
        }

        IntPtr result = Shell32.ShellExecuteW(
            IntPtr.Zero,
            "open",
            "notepad.exe",
            $"\"{path}\"",
            null,
            Win32Constants.SW_SHOWNORMAL);

        if ((long)result <= 32)
            Logger.Warning($"ShellExecuteW failed for notepad (rc={(long)result}, path={path})");
        else
            Logger.Info($"Opened config file in notepad: {path}");
    }

    // ================================================================
    // Private — 기본 위치 설정
    // ================================================================

    /// <summary>
    /// 현재 인디케이터 위치를 가장 가까운 모서리 기준으로 환산하여 기본 위치로 저장.
    /// 고정 모드 → work area 기준, 창 기준 모드 → 포그라운드 창 기준.
    /// 인디가 한 번도 표시된 적이 없으면 경고.
    /// </summary>
    private static void SetDefaultPositionToCurrent(AppConfig config, IntPtr hwndForeground,
        Action<AppConfig> updateConfig)
    {
        if (config.PositionMode == PositionMode.Window)
        {
            RelativePositionConfig? rel =
                Overlay.ComputeRelativeFromCurrentPosition(hwndForeground);
            if (rel is null)
            {
                ShowPositionError();
                return;
            }
            updateConfig(config with { DefaultIndicatorPositionRelative = rel });
            Logger.Info($"Default relative position saved: corner={rel.Corner}, "
                      + $"delta=({rel.DeltaX}, {rel.DeltaY}) logical px");
        }
        else
        {
            DefaultPositionConfig? anchor = Overlay.ComputeAnchorFromCurrentPosition();
            if (anchor is null)
            {
                ShowPositionError();
                return;
            }
            updateConfig(config with { DefaultIndicatorPosition = anchor });
            Logger.Info($"Default indicator position saved: corner={anchor.Corner}, "
                      + $"delta=({anchor.DeltaX}, {anchor.DeltaY}) logical px");
        }
    }

    private static void ShowPositionError()
    {
        // MessageBoxW 는 자체 메시지 루프를 돌리므로 ModalDialogLoop.Run 으로 감쌀 수 없다.
        // RunExternal 로 IsActive 가드만 씌워, 박스가 열린 동안 감지 스레드가 인디를
        // 박스 근처로 튀게 만들지 않도록 한다.
        ModalDialogLoop.RunExternal(_hwndMain, () =>
            User32.MessageBoxW(_hwndMain, I18n.TrayPositionUnavailable, "KoEnVue", 0));
    }

    // ================================================================
    // Private — 툴팁
    // ================================================================

    /// <summary>
    /// 현재 IME 상태 기반 툴팁 문자열을 반환한다. <c>config.TrayTooltip</c> 이 false 이면 null
    /// (shell 은 빈 툴팁으로 취급하여 호버 표시를 생략).
    /// </summary>
    private static string? BuildTooltip(ImeState state, AppConfig config)
    {
        if (!config.TrayTooltip) return null;
        return $"KoEnVue - {I18n.GetTrayTooltip(state)}";
    }

    // ================================================================
    // Private — 빠른 투명도 프리셋 적용
    // ================================================================

    /// <summary>
    /// 빠른 투명도 프리셋을 DisplayMode에 맞게 적용한다.
    /// Always 모드에서는 ActiveOpacity를 프리셋 값으로, IdleOpacity를 기존 비율 유지하며 변경.
    /// OnEvent 모드에서는 Opacity만 변경.
    /// </summary>
    private static AppConfig ApplyQuickOpacity(AppConfig config, double preset)
    {
        if (config.DisplayMode == DisplayMode.Always)
        {
            double idleRatio = config.ActiveOpacity > OpacityTolerance
                ? config.IdleOpacity / config.ActiveOpacity
                : 0.0;
            return config with
            {
                Opacity = preset,
                ActiveOpacity = preset,
                IdleOpacity = Math.Clamp(preset * idleRatio, 0.1, 1.0)
            };
        }
        return config with { Opacity = preset };
    }

    // ================================================================
    // Private — 시작 프로그램 등록 (schtasks)
    // ================================================================

    private static bool IsStartupRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(SchtasksQueryTimeoutMs);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException)
        {
            // 정책 항목 1(타입 좁히기): Process.Start 실패(스케쥴러 없음/실행 권한 없음) 만 false 로 폴백.
            // 로직 버그(NullRef 등)는 propagate 시켜 표면화.
            return false;
        }
    }

    private static void ToggleStartupRegistration()
    {
        try
        {
            if (IsStartupRegistered())
            {
                // 삭제
                RunSchtasks($"/delete /tn \"{TaskName}\" /f");
                Logger.Info("Startup registration removed");
            }
            else
            {
                // 등록
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                RegisterStartupTaskWithXml(exePath);
                Logger.Info("Startup registration created");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException
            or IOException or UnauthorizedAccessException)
        {
            // 정책 항목 1(타입 좁히기): schtasks.exe 실행 실패 + 임시 XML 파일 write 실패만 잡음.
            Logger.Warning($"Failed to toggle startup registration: {ex.Message}");
        }
    }

    /// <summary>
    /// LogonTrigger.Delay 를 포함한 Task Scheduler 2.0 XML 을 생성해 schtasks /xml 로 등록한다.
    /// /tr 방식과 달리 초 단위 지연이 지정 가능 — Shell(explorer) 트레이 초기화 레이스 회피.
    /// </summary>
    private static void RegisterStartupTaskWithXml(string exePath)
    {
        string xml = BuildStartupTaskXml(exePath);
        // 프로세스별 유니크 이름으로 동시 등록 충돌 방지. %TEMP% 는 per-user 이므로 다른 사용자 간
        // 충돌도 없다.
        string tempPath = Path.Combine(Path.GetTempPath(), $"koenvue-task-{Environment.ProcessId}.xml");
        try
        {
            // schtasks /xml 은 UTF-16 LE + BOM 을 기대한다. Encoding.Unicode 가 정확히 그 포맷.
            File.WriteAllText(tempPath, xml, System.Text.Encoding.Unicode);
            RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{tempPath}\" /f");
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    /// <summary>
    /// schtasks /xml 에 전달할 Task Scheduler 2.0 XML 을 조립한다. LogonTrigger.Delay 에
    /// <see cref="StartupTaskDelay"/> 삽입. 최소 필드만 쓰고 나머지는 schtasks 기본값에 위임.
    /// </summary>
    private static string BuildStartupTaskXml(string exePath)
    {
        string userId = EscapeXml($"{Environment.UserDomainName}\\{Environment.UserName}");
        // /tr 방식의 기존 Command 형식("\"path\"")을 XML 에서도 동일하게 유지 — QueryRegisteredTask 의
        // Trim('"') 로직이 두 방식 모두 호환되어 마이그레이션 후에도 경로 비교가 안정적.
        string command = EscapeXml($"\"{exePath}\"");
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>{StartupTaskDelay}</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{userId}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");

    /// <summary>
    /// 등록된 시작 프로그램 태스크의 exe 경로가 현재 실행 파일 경로와 다르면 재등록한다.
    /// 포터블 모드에서 exe를 다른 폴더로 옮겼을 때 태스크 스케줄러가 오래된 절대 경로를 가리키는 문제를 해결.
    /// schtasks 호출 지연(~100~300ms)을 main 스레드에서 분리하기 위해 백그라운드 스레드에서 실행.
    /// </summary>
    internal static void SyncStartupPathAsync()
    {
        var thread = new Thread(SyncStartupPathCore)
        {
            IsBackground = true,
            Name = "StartupPathSync",
        };
        thread.Start();
    }

    private static void SyncStartupPathCore()
    {
        try
        {
            var (registeredCommand, registeredDelay) = QueryRegisteredTask();
            if (registeredCommand is null)
            {
                // 등록 안 돼 있거나 쿼리 실패 — 정상 케이스 (대부분 사용자는 startup 안 씀)
                return;
            }

            // 구 버전(/tr 방식) 및 신 버전(/xml + "\"path\"" 보존) 모두 Command 양끝에 리터럴 큰따옴표가
            // 있다. Path.GetFullPath 는 " 를 잘못된 문자로 보고 ArgumentException 을 던져 PathsEqual 이
            // 원본 문자열 비교로 폴백하므로, 비교 전 감싼 따옴표를 제거해 매 부팅마다 재등록되는 것을 막는다.
            string registeredPath = registeredCommand.Trim('"');

            string? currentPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentPath)) return;

            bool pathMatches = PathsEqual(registeredPath, currentPath);
            // 구 버전(/tr 방식)은 <Delay> 요소가 없어 null. 이 경우도 마이그레이션 대상.
            bool delayMatches = string.Equals(registeredDelay, StartupTaskDelay, StringComparison.Ordinal);

            if (pathMatches && delayMatches)
            {
                Logger.Debug("Startup task already in sync (path + delay)");
                return;
            }

            Logger.Info(
                $"Startup task out of sync (path='{registeredPath}' delay='{registeredDelay ?? "<none>"}'), re-registering with delay {StartupTaskDelay}");
            RegisterStartupTaskWithXml(currentPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException
            or IOException or UnauthorizedAccessException)
        {
            // 정책 항목 1(타입 좁히기): schtasks.exe 실행 실패 + 임시 XML 파일 write 실패만 잡음.
            Logger.Warning($"Failed to sync startup task path: {ex.Message}");
        }
    }

    /// <summary>
    /// 등록된 시작 프로그램 태스크의 실행 명령 경로를 반환. 미등록 또는 실패 시 null.
    /// </summary>
    private static (string? Command, string? Delay) QueryRegisteredTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /xml ONE")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (null, null);
            string xml = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(SchtasksQueryTimeoutMs);
            if (proc.ExitCode != 0) return (null, null);
            return (ExtractTagFromXml(xml, "Command", unescape: true),
                    ExtractTagFromXml(xml, "Delay", unescape: false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException
            or FileNotFoundException
            or IOException)
        {
            // schtasks.exe 실행/stdout 파이프 읽기 실패만 흡수 → null(미등록으로 취급).
            // 로직 버그는 전파.
            _ = ex;
            return (null, null);
        }
    }

    /// <summary>
    /// schtasks XML 출력에서 지정한 단일 태그의 내용을 추출한다.
    /// <paramref name="unescape"/>가 true 면 XML 엔티티(&amp;amp; 등)를 복원한다 —
    /// Command 경로엔 필요하지만 Delay(ISO 8601) 같은 순수 텍스트에는 불필요.
    /// </summary>
    private static string? ExtractTagFromXml(string xml, string tagName, bool unescape)
    {
        string openTag = $"<{tagName}>";
        string closeTag = $"</{tagName}>";
        int start = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (start < 0) return null;
        start += openTag.Length;
        int end = xml.IndexOf(closeTag, start, StringComparison.Ordinal);
        if (end < 0) return null;

        string content = xml[start..end];
        if (unescape)
        {
            content = content
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">");
        }
        return content.Trim();
    }

    /// <summary>
    /// Windows 경로 동일성 비교 (대소문자 무시 + 정규화).
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
            or System.Security.SecurityException
            or PathTooLongException
            or NotSupportedException)
        {
            // 경로 정규화 실패(잘못된 문자/권한/길이 초과/플랫폼 미지원) 시 원본 문자열 비교로 폴백.
            // 로직 버그는 전파.
            _ = ex;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ================================================================
    // Private — 위치 기록 정리
    // ================================================================

    /// <summary>
    /// indicator_positions 전체 항목을 체크박스 다이얼로그로 표시하여 선택 삭제한다.
    /// 실행 중인 프로세스에는 "(실행 중)" / "(running)" 접미사를 붙여 표시한다.
    /// </summary>
    private static void CleanupPositions(AppConfig config, Action<AppConfig> updateConfig)
    {
        // 양쪽 dict의 키 합집합
        var allKeys = new HashSet<string>(config.IndicatorPositions.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string k in config.IndicatorPositionsRelative.Keys)
            allKeys.Add(k);

        if (allKeys.Count == 0)
        {
            // MessageBoxW 는 자체 메시지 루프를 돌리므로 ModalDialogLoop.Run 으로 감쌀 수 없다.
            // RunExternal 로 IsActive 가드만 씌워 박스가 열린 동안 인디를 억제.
            ModalDialogLoop.RunExternal(_hwndMain, () =>
                User32.MessageBoxW(_hwndMain, I18n.TrayPositionHistoryEmpty, "KoEnVue", 0));
            return;
        }

        // 실행 중인 프로세스 이름 수집
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try { running.Add(proc.ProcessName); }
                catch (Exception ex) when (ex is InvalidOperationException
                                             or System.ComponentModel.Win32Exception)
                {
                    Logger.Debug($"CleanupDialog: failed to read process name: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"CleanupDialog: Process.GetProcesses enumeration failed: {ex.Message}");
        }

        // 전체 항목에 실행 중 여부 접미사 추가
        string runningSuffix = I18n.RunningSuffix;
        var displayItems = new List<string>();
        var originalNames = new List<string>();
        foreach (string name in allKeys)
        {
            originalNames.Add(name);
            displayItems.Add(running.Contains(name) ? name + runningSuffix : name);
        }

        // 체크박스 다이얼로그 표시
        List<string>? selected = CleanupDialog.Show(_hwndMain, displayItems);
        if (selected is null || selected.Count == 0) return;

        // 선택된 표시 이름에서 원본 이름 복원 후 삭제
        var selectedOriginal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < displayItems.Count; i++)
        {
            if (selected.Contains(displayItems[i]))
                selectedOriginal.Add(originalNames[i]);
        }

        var cleanedFixed = new Dictionary<string, int[]>(config.IndicatorPositions);
        var cleanedRelative = new Dictionary<string, int[]>(config.IndicatorPositionsRelative);
        foreach (string name in selectedOriginal)
        {
            cleanedFixed.Remove(name);
            cleanedRelative.Remove(name);
        }

        updateConfig(config with
        {
            IndicatorPositions = cleanedFixed,
            IndicatorPositionsRelative = cleanedRelative,
        });
        Logger.Info($"Cleaned {selectedOriginal.Count} position(s): {string.Join(", ", selectedOriginal)}");
    }

    private static void RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(SchtasksCommandTimeoutMs);
    }

}
