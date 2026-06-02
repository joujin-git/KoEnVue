using KoEnVue.App.Config;
using KoEnVue.App.Localization;
using KoEnVue.App.Models;
using KoEnVue.App.Startup;
using KoEnVue.App.UI.Dialogs;
using KoEnVue.App.Update;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;
using KoEnVue.Core.Shell;
using KoEnVue.Core.Tray;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI;

/// <summary>
/// Shell_NotifyIconW 기반 시스템 트레이 아이콘 관리 + 팝업 메뉴 + 메뉴 커맨드 디스패치.
/// WinForms NotifyIcon 사용 금지 (P1). PR-04 분해 후 시작 프로그램 등록 →
/// <see cref="StartupTaskManager"/>, 위치 정리 → <see cref="PositionCleanupService"/>,
/// URL/파일 열기 → <see cref="UriLauncher"/> 로 위임.
/// </summary>
internal static partial class Tray
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
    private const int IDM_USER_HIDDEN        = 4009;
    private const int IDM_CURSOR_TOGGLE      = 4011;
    /// <summary>PR-21: 커서 인디 IME 전환 스케일 팝 on/off 토글 (메인 ChangeHighlight 와 동형).</summary>
    private const int IDM_CURSOR_HIGHLIGHT   = 4013;
    /// <summary>PR-15: admin_elevation 토글 (UIPI 우회용 관리자 권한 실행 옵션).</summary>
    private const int IDM_ADMIN_ELEVATION    = 4012;
    // IDM_HOMEPAGE: 메뉴 최상단 헤더 라인의 단일 진입점. `_pendingUpdate` 상태에 따라
    // OpenUpdatePage(릴리스 페이지) / OpenHomepage(레포 루트) 로 동적 분기.
    // 4008 슬롯은 v0.9.2.5 까지 IDM_UPDATE_DOWNLOAD 가 점유했으나 헤더 통합으로 dead 가 되어 제거 —
    // 의미 충돌 방지 목적의 의도적 빈 자리로 둔다.
    private const int IDM_HOMEPAGE           = 4010;
    private const int IDM_EXIT               = 4002;

    // P3: 매직 넘버 금지
    private const double OpacityTolerance = 0.001;

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
    /// 트레이 아이콘 등록 (NIM_ADD + NIM_SETVERSION). <c>config.TrayEnabled == false</c> 이면 건너뛴다.
    /// NIM_ADD 실패 시 WM_TIMER 로 1초 간격 재시도 — explorer 트레이 초기화 전에 기동된 부팅 레이스 대비.
    /// TaskbarCreated 브로드캐스트를 못 받는 환경에서도 복구 가능.
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
    /// WM_TIMER(TIMER_ID_TRAY_ADD_RETRY) 핸들러. 첫 NIM_ADD 실패 시 1초 간격 재시도, 성공/한도 도달 시 해제.
    /// TaskbarCreated 가 선행 도착하면 Recreate 경로에서 _addPending 이 정리되어 본 타이머도 자연스럽게 stop.
    /// </summary>
    internal static void HandleAddRetryTimer()
    {
        if (!_addPending || _notifyIcon is null || _currentIcon is null || _pendingConfig is null)
        {
            StopAddRetryTimer();
            return;
        }

        _addRetryCount++;
        // _pendingConfig 는 Initialize 실패 경로에서 반드시 설정됨 — null 가능성은 위 가드가 차단.
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

        // 이전 아이콘 해제 후 교체 — 소유권은 Tray 측에 남는다 (NotifyIconManager 는 해제 금지).
        _currentIcon?.Dispose();
        _currentIcon = newIcon;

        // Add 재시도 중이면 pending 상태도 최신화 — 재시도 성공 후 툴팁이 오래된 상태로 남는 걸 방지.
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
    /// 트레이 아이콘 재등록 (Explorer 재시작/크래시로 셸이 아이콘을 잃을 때 TaskbarCreated 수신 후).
    /// 셸 측에 이전 등록이 없으므로 NIM_DELETE 가 실패해도 무해.
    /// </summary>
    internal static void Recreate(ImeState state, AppConfig config)
    {
        // TaskbarCreated 가 Initialize 이전에 도착하는 레이스에서 _hwndMain 만 세팅된 상태일 수 있다.
        // _initialized 까지 확인하지 않으면 Initialize 재호출 시 _currentIcon 참조 유실로 핸들 누수.
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

        // 재시도 타이머 정리 — Recreate 경로에서 이전 retry 상태가 새 초기화에 섞이지 않도록.
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

    // ShowMenu(메뉴 빌더) 는 partial 분할 — App/UI/Tray.Menu.cs 참조.

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
                StartupTaskManager.ToggleStartupRegistration(config);
                break;

            // --- 관리자 권한 토글 (PR-15 후속 fix #3, 2026-05-29: 4 case 통일) ---
            // 흐름: config 토글 + schtasks 재등록 + 통일 안내 + WM_CLOSE 자동 종료.
            // 자동 spawn 안 함 — Windows token 모델의 admin→일반 down-grade 한계 자연 회피.
            // 사용자가 수동 재실행 시 새 옵션 적용 (일반 권한 재실행 + config=true → TryRelaunchAsAdmin
            // self-elevation UAC 1회 / config=false → 일반 권한 유지). admin 환경 재실행은 토큰 상속
            // (KoEnVue 통제 외) — PR-15 §7.2 의 down-grade 한계 그대로 보존.
            case IDM_ADMIN_ELEVATION:
                {
                    AppConfig newAdminConfig = config with { AdminElevation = !config.AdminElevation };
                    updateConfig(newAdminConfig);
                    // schtasks 의 RunLevel 즉시 갱신 — 등록 안 됐으면 noop.
                    StartupTaskManager.ReregisterIfAdminChanged(newAdminConfig);

                    User32.MessageBoxW(hwndMain,
                        I18n.AdminElevationChangeNotice, DefaultConfig.AppName,
                        Win32Constants.MB_OK);

                    // "확인" 후 자동 종료 — 메인 인디 잔존 회귀 차단 + 사용자 mental model 정합.
                    User32.PostMessageW(hwndMain, Win32Constants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
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

            // --- 커서 인디케이터 숨김 토글 (메뉴 체크박스 — MF_CHECKED = 현재 숨김 상태) ---
            // 메인 인디 IDM_USER_HIDDEN 과 같은 패턴. 라벨 "커서 인디케이터 숨김" + 체크 = 안 보임.
            // 클릭 시 enabled 반전 (체크 ON → enabled=true → 표시, 체크 OFF → enabled=false → 숨김).
            case IDM_CURSOR_TOGGLE:
                updateConfig(config with { CursorIndicatorEnabled = !config.CursorIndicatorEnabled });
                Logger.Info($"CursorIndicatorEnabled toggled via menu: {!config.CursorIndicatorEnabled}");
                break;

            // --- 커서 변경 시 강조(스케일 팝) 토글 (체크 = ON, 메인 ChangeHighlight 와 동형) ---
            case IDM_CURSOR_HIGHLIGHT:
                updateConfig(config with { CursorChangeHighlight = !config.CursorChangeHighlight });
                Logger.Info($"CursorChangeHighlight toggled via menu: {!config.CursorChangeHighlight}");
                break;

            // --- 상세 설정 ---
            case IDM_SETTINGS:
                SettingsDialog.Show(hwndMain, config, updateConfig);
                break;

            // --- 종료 ---
            case IDM_EXIT:
                User32.PostQuitMessage(0);
                break;

            // --- 메뉴 최상단 헤더 클릭 — 새 버전 가용 시 릴리스 페이지, 평소엔 레포 루트 ---
            case IDM_HOMEPAGE:
                if (_pendingUpdate is not null)
                    OpenUpdatePage();
                else
                    OpenHomepage();
                break;
        }
    }

    /// <summary>
    /// 펜딩된 업데이트의 GitHub 릴리스 페이지를 기본 브라우저로 연다.
    /// <para>
    /// <b>URL 프리픽스 검증</b> — <c>info.HtmlUrl</c> 은 GitHub API 응답에서 왔다. MITM/계정 탈취 시
    /// <c>file:///</c>·<c>javascript:</c>·<c>ms-settings:</c> 스킴이 주입될 수 있어, 예상 릴리스 페이지
    /// 프리픽스 (<c>https://github.com/{owner}/{name}/</c>) 와 일치하지 않으면 열지 않는다. PR-03 후
    /// Admin 토큰 EoP 는 사라졌지만 사용자 컨텍스트 임의 핸들러 실행 방지 목적으로 검증 유지.
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

        UriLauncher.Open(info.HtmlUrl);
    }

    /// <summary>
    /// GitHub 레포 홈페이지를 기본 브라우저로 연다 (트레이 메뉴 헤더 "KoEnVue v… — GitHub" 클릭).
    /// URL 은 컴파일 타임 상수에서 합성하므로 <see cref="OpenUpdatePage"/> 와 달리 prefix 검증 불필요.
    /// </summary>
    private static void OpenHomepage() =>
        UriLauncher.Open($"https://github.com/{DefaultConfig.UpdateRepoOwner}/{DefaultConfig.UpdateRepoName}");

    /// <summary>
    /// 현재 활성 config.json 을 메모장으로 연다 (트레이 좌클릭 "설정 파일 열기" 동작).
    /// <para>
    /// <b>메모장 고정 이유</b> — 일반 사용자 PC 에는 <c>.json</c> 에 연결된 기본 앱이 없어
    /// shell 의 <c>open</c> verb 가 "앱 선택" 다이얼로그를 띄우거나 무반응으로 보일 확률이 높다.
    /// 메모장은 Windows 모든 버전에 기본 탑재되고 UTF-8 표시 + 저장 시 hot reload 감지가 즉시 된다.
    /// 경로에 공백이 있을 수 있어 인자를 따옴표로 감싼다.
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

        UriLauncher.Open("notepad.exe", $"\"{path}\"");
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

    /// <summary>
    /// 트레이 안내 MessageBox (제목 = 앱명, 확인 버튼). MessageBoxW 는 자체 메시지 루프를
    /// 돌려 ModalDialogLoop.Run 으로 감쌀 수 없으므로 RunExternal 로 IsActive 가드만 씌워
    /// 박스가 열린 동안 감지 스레드의 인디 튐을 억제한다. RunExternal 가드를 단일 경로로
    /// 모아 호출처마다 누락되지 않도록 한다.
    /// </summary>
    private static void ShowMessage(string body)
        => ModalDialogLoop.RunExternal(_hwndMain, () =>
            User32.MessageBoxW(_hwndMain, body, DefaultConfig.AppName, uType: Win32Constants.MB_OK));

    private static void ShowPositionError()
        => ShowMessage(I18n.TrayPositionUnavailable);

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
    /// 빠른 투명도 프리셋 적용. Always 모드는 Active/Idle 의 기존 비율 유지하며 변경, OnEvent 는 Opacity 만.
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
                IdleOpacity = Math.Clamp(preset * idleRatio, DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity)
            };
        }
        return config with { Opacity = preset };
    }

    // ================================================================
    // Private — 위치 기록 정리
    // ================================================================

    /// <summary>
    /// indicator_positions 정리 대화상자 — empty 안내 + dialog 띄우기. 비즈니스 로직은
    /// <see cref="PositionCleanupService"/> 로 위임.
    /// </summary>
    private static void CleanupPositions(AppConfig config, Action<AppConfig> updateConfig)
    {
        var (displayItems, originalNames) = PositionCleanupService.Compute(config);
        if (displayItems.Count == 0)
        {
            ShowMessage(I18n.TrayPositionHistoryEmpty);
            return;
        }

        List<string>? selected = CleanupDialog.Show(_hwndMain, displayItems);
        if (selected is null || selected.Count == 0) return;

        updateConfig(PositionCleanupService.RemoveSelected(config, displayItems, originalNames, selected));
    }
}
