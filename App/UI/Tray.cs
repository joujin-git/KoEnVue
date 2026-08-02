using KoEnVue.App.Bootstrap;
using KoEnVue.App.Config;
using KoEnVue.App.Localization;
using KoEnVue.App.Messaging;
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
    /// <summary>PR-21: 커서 헤일로 IME 전환 스케일 팝 on/off 토글 (메인 ChangeHighlight 와 동형).</summary>
    private const int IDM_CURSOR_HIGHLIGHT   = 4013;
    /// <summary>PR-31: 커서 표시 Soft / Sharp / Motion 라디오.</summary>
    private const int IDM_CURSOR_DISPLAY_SOFT   = 4014;
    private const int IDM_CURSOR_DISPLAY_SHARP  = 4015;
    private const int IDM_CURSOR_DISPLAY_MOTION = 4016;
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

    // Shell_NotifyIconW 재진입 가드 (UI 스레드 전용 — 락 불필요).
    //
    // Shell_NotifyIconW 는 explorer 로 가는 **블로킹 크로스프로세스 SendMessage** 라, 호출 스레드가
    // 그 동안 이 스레드로 들어온 *sent* 메시지를 계속 디스패치한다. WM_SETTINGCHANGE /
    // WM_THEMECHANGED 는 시스템·다른 프로세스가 SendMessageTimeout(HWND_BROADCAST, …) 로 **보내는**
    // 메시지라 정확히 그 구간에 배달되고, Program.HandleSettingChange 가 다시 Tray.UpdateState 로
    // 들어온다. 그러면 _currentIcon 을 「셸에 넘기고 → 해제하고 → 재대입」 하는 세 걸음이 뒤엉켜
    // **셸이 지금 그리고 있는 HICON 을 파괴**한다 (bug-hunt 2026-08-02 확정 #8·#42).
    //
    // 재진입을 버리지 않는 이유: 안쪽 호출이 더 **새로운** 상태(방금 바뀐 테마 색)를 들고 온다.
    // 표시만 남기고 바깥 프레임이 끝날 때 그 값으로 한 번 더 갱신한다.
    //
    // **가드와 보류 소비는 반드시 한 쌍이다** (bug-hunt 3차 A·B). 종전에는 가드를 세우는 곳이 둘
    // (UpdateState · HandleAddRetryTimer)인데 보류를 소비·정리하는 곳은 UpdateState 하나뿐이었다.
    // 재시도 프레임에 들어온 갱신은 그 프레임에서 유실되고, 표식만 프레임 밖으로 살아남아 **다음**
    // UpdateState 가 방금 그린 최신 아이콘 위에 몇 초 전 상태를 덮어썼다. 게다가 같은 블로킹
    // Shell_NotifyIconW 를 부르는 Initialize·Remove 는 가드 자체가 없었다. 이제 셸 호출 구간은
    // 전부 RunShellCall 하나를 지나간다 (P4).
    private static bool _shellCallInProgress;
    private static bool _updatePending;
    private static ImeState _pendingUpdateState;
    private static AppConfig? _pendingUpdateConfig;

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

        SafeIconHandle icon = TrayIcon.CreateIcon(initialState, config);
        _currentIcon = icon;

        NotifyIconManager notify = new(hwndMain, AppMessages.WM_TRAY_CALLBACK, DefaultConfig.AppGuid);
        _notifyIcon = notify;

        // **무효 HICON 을 셸에 등록하지 않는다.** TrayIcon.CreateIcon 은 GDI 실패 시
        // SafeIconHandle(IntPtr.Zero) 를 돌려주는데, 그것을 NIF_ICON 과 함께 NIM_ADD 하면 셸이 빈 칸을
        // 그리고 아무도 재생성하지 않는다 — 다음 IME 전환이나 config 변경으로 UpdateState 가 불릴
        // 때까지 그대로다. UpdateState 는 같은 상황을 IsInvalid 가드로 걸러 이전 아이콘을 유지하는데,
        // **최초 등록 경로만 그 정책에서 빠져 있었다** (확정 #13·#44). 부팅 자동 시작(schtasks
        // LogonTrigger)은 explorer 초기화 전이라 NIM_ADD 실패 구간과 GDI 압박 구간이 겹친다.
        // 무효면 Add 를 건너뛰고 아래 재시도 경로로 보낸다 — 재시도가 아이콘부터 다시 만든다.
        //
        // **_initialized 를 셸 호출 앞에 세운다** (bug-hunt 3차 B). NIM_ADD 는 블로킹 IPC 라 그 구간에
        // 재진입이 들어오는데, 종전에는 _initialized 가 아직 false 라 UpdateState 가 첫 줄에서 조용히
        // 사라졌다 — 등록 직후 상태가 이미 낡아 있어도 반영할 길이 없었다. 이제 보류 표식으로 남고
        // RunShellCall 의 드레인이 최신 값으로 한 번 더 갱신한다.
        _initialized = true;

        bool added = false;
        RunShellCall(
            () => added = !icon.IsInvalid
                          && notify.Add(icon.DangerousGetHandle(), BuildTooltip(initialState, config)),
            drainPending: true);

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
        if (!_addPending || _notifyIcon is null || _pendingConfig is null)
        {
            StopAddRetryTimer();
            return;
        }

        // NIM_ADD 도 블로킹 크로스프로세스 SendMessage 다 — UpdateState 가 셸 호출 중이면 이번 틱은
        // 건너뛴다. 타이머가 1초 뒤 다시 온다 (확정 #42 의 HandleAddRetryTimer 측 동형 패턴).
        if (_shellCallInProgress) return;

        _addRetryCount++;

        // **아이콘을 다시 만든다.** 최초 실패가 GDI 자원 고갈이었다면 핸들 자체가 무효라, 같은
        // 핸들로 30회를 반복해 봐야 전부 무의미하고 설령 Add 가 성공해도 빈 칸이 박힌다. 압박이
        // 풀리는 시점이 곧 재시도가 의미를 갖는 시점이다 (확정 #44).
        if (_currentIcon is null or { IsInvalid: true })
        {
            _currentIcon?.Dispose();
            _currentIcon = TrayIcon.CreateIcon(_pendingInitialState, _pendingConfig);
        }

        if (_currentIcon.IsInvalid)
        {
            // 아직 만들 수 없다 — NULL 을 넘기면 빈 칸이 고착되므로 이번 회차는 등록하지 않는다.
            if (_addRetryCount >= TrayAddRetryMaxAttempts)
            {
                Logger.Warning($"Tray icon creation still failing after {_addRetryCount} retries; giving up");
                StopAddRetryTimer();
            }
            return;
        }

        bool added = false;
        NotifyIconManager notify = _notifyIcon;
        SafeIconHandle icon = _currentIcon;
        ImeState pendingState = _pendingInitialState;
        AppConfig pendingConfig = _pendingConfig;
        RunShellCall(
            () => added = notify.Add(icon.DangerousGetHandle(), BuildTooltip(pendingState, pendingConfig)),
            drainPending: true);

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

        // 셸 호출 중 재진입 — 표시만 남기고 돌아간다. 자세한 이유는 _shellCallInProgress 선언부.
        if (_shellCallInProgress)
        {
            _updatePending = true;
            _pendingUpdateState = state;
            _pendingUpdateConfig = config;
            return;
        }

        RunShellCall(() => UpdateStateCore(state, config), drainPending: true);
    }

    /// <summary>
    /// 블로킹 <c>Shell_NotifyIconW</c> 구간을 재진입 가드와 함께 실행하는 <b>단일 진입점</b> (P4).
    /// 최외곽 프레임만 보류 표식을 소비·정리한다 — 중첩 호출이 바깥 프레임의 가드를 먼저 풀어
    /// 버리면 그 구간의 재진입이 다시 열린다.
    ///
    /// <para>
    /// <paramref name="drainPending"/> 이 false 인 경우는 <see cref="Remove"/> 하나다. 아이콘을
    /// 없애는 중에 보류분을 재생하면 방금 지운 아이콘을 되살리게 되고, 그 시점
    /// <c>_currentIcon</c>·<c>_notifyIcon</c> 은 이미 정리된 뒤다. <b>그렇다고 표식을 지우지도
    /// 않는다</b> — <see cref="Recreate"/> 가 Remove 직후 Initialize 를 부르므로, 제거 구간에 들어온
    /// 더 새로운 상태는 그 Initialize 의 드레인이 소비해야 한다. 종료 경로에서 남는 표식은
    /// <c>_initialized == false</c> 라 아무도 읽지 않는다.
    /// </para>
    /// </summary>
    private static void RunShellCall(Action call, bool drainPending)
    {
        bool outermost = !_shellCallInProgress;
        _shellCallInProgress = true;
        try
        {
            call();

            // 재진입이 들고 온 더 새로운 상태를 여기서 소비한다 (테마 색 변경 등).
            if (drainPending && outermost)
            {
                while (_updatePending)
                {
                    _updatePending = false;
                    AppConfig pending = _pendingUpdateConfig!;
                    UpdateStateCore(_pendingUpdateState, pending);
                }
            }
        }
        finally
        {
            if (outermost)
            {
                _shellCallInProgress = false;
                if (drainPending)
                {
                    _updatePending = false;
                    _pendingUpdateConfig = null;
                }
            }
        }
    }

    private static void UpdateStateCore(ImeState state, AppConfig config)
    {
        var newIcon = TrayIcon.CreateIcon(state, config);

        // 생성 실패(NULL 핸들)면 직전 유효 아이콘을 유지한다 — NULL 을 NIM_MODIFY 로 넘기면 셸이
        // 빈 칸을 그리고, _currentIcon 까지 무효 핸들로 덮이면 다음 성공까지 복구되지 않는다.
        // 툴팁은 이전 핸들로 갱신해 IME 상태 텍스트만은 최신을 유지. 렌더 엔진의 "이전 DIB 유지"
        // (LayeredOverlayBase) 와 같은 우아한 열화 패턴.
        if (newIcon.IsInvalid)
        {
            Logger.Warning("Tray icon creation failed; keeping previous icon");
            newIcon.Dispose();
            if (_currentIcon is { IsInvalid: false })
                _notifyIcon?.UpdateIconAndTooltip(_currentIcon.DangerousGetHandle(), BuildTooltip(state, config));
            if (_addPending)
            {
                _pendingInitialState = state;
                _pendingConfig = config;
            }
            return;
        }

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

        // **_initialized 를 셸 호출 앞에 내린다** (bug-hunt 3차 B). NIM_DELETE 도 블로킹 IPC 이고,
        // 종전에는 이 플래그가 true 인 채로 그 구간에 들어가 재진입한 UpdateState 가 본체까지 내려가
        // 방금 넘긴 HICON 을 만졌다. 여기서 내려 두면 재진입은 첫 줄에서 즉시 돌아간다.
        _initialized = false;

        bool removed = true;
        NotifyIconManager? notify = _notifyIcon;
        // 제거 중에는 보류분을 재생하지 않는다 — 방금 지운 아이콘을 되살리는 셈이고, 아래에서
        // _currentIcon·_notifyIcon 이 정리된다.
        RunShellCall(() => removed = notify?.Remove() ?? true, drainPending: false);

        if (!removed)
            Logger.Warning("Failed to remove tray icon on shutdown");

        _currentIcon?.Dispose();
        _currentIcon = null;
        _notifyIcon = null;

        Logger.Info("Tray icon removed");
    }

    // ShowMenu(메뉴 빌더) 는 partial 분할 — App/UI/Tray.Menu.cs 참조.

    /// <summary>
    /// WM_COMMAND 메뉴 명령 처리.
    /// config 변경이 필요한 항목은 updateConfig 콜백으로 Program.cs에 위임.
    /// </summary>
    internal static void HandleMenuCommand(int commandId, AppConfig config, IntPtr hwndMain,
        IntPtr hwndForeground, Func<AppConfig> currentConfig, Action<AppConfig> updateConfig)
    {
        // 명령 적용의 베이스는 **지금** 값이어야 한다. 호출자가 넘긴 config 는 메뉴를 띄울 때의
        // 스냅샷인데, TrackPopupMenu 는 자체 모달 루프를 돌리므로 메뉴가 열려 있는 동안 감지
        // 스레드가 post 한 WM_CONFIG_CHANGED 가 그대로 디스패치돼 _config 가 교체될 수 있다.
        // 그 경우 아래 24곳의 `config with { … }` 가 옛 베이스 위에 얹혀 방금 반영된 외부 편집을
        // 조용히 되돌리고, 뒤따르는 Settings.Save 가 디스크까지 덮는다 (AUDIT-2026-07-30 §B).
        config = currentConfig();

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
                // 다이얼로그가 **자체 모달 루프**를 돌았으므로 그 사이 WM_CONFIG_CHANGED 가 처리돼
                // config 가 교체됐을 수 있다. 진입 시 한 번 잡은 값은 여기서 이미 stale 이다
                // (bug-hunt 2026-08-02 확정 #16 — §B 수정이 함수 진입 1회만 다시 읽던 한계).
                config = currentConfig();

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
                // 부팅 직후 배경 경로 동기화와 겹치면 조작이 무시된다 — 조용히 넘어가면 "눌렀는데
                // 아무 일도 안 일어난다" 가 되므로 알린다 (bug-hunt 2026-08-02 확정 #4).
                if (!StartupTaskManager.ToggleStartupRegistration(config))
                    ShowMessage(I18n.StartupTaskBusy);
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

                    ShowMessage(I18n.AdminElevationChangeNotice);

                    // "확인" 후 자동 종료 — 플로팅 배지 잔존 회귀 차단 + 사용자 mental model 정합.
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
                UpdateIfChanged(updateConfig, config.PositionMode != PositionMode.Fixed,
                    config with { PositionMode = PositionMode.Fixed }, "Position mode changed to Fixed");
                break;

            // --- 위치 모드: 창 기준 ---
            case IDM_POSITION_WINDOW:
                UpdateIfChanged(updateConfig, config.PositionMode != PositionMode.Window,
                    config with { PositionMode = PositionMode.Window }, "Position mode changed to Window");
                break;

            // --- 드래그 활성 키 ---
            case IDM_DRAG_MOD_NONE:
                UpdateIfChanged(updateConfig, config.DragModifier != DragModifier.None,
                    config with { DragModifier = DragModifier.None }, "DragModifier changed to None");
                break;
            case IDM_DRAG_MOD_CTRL:
                UpdateIfChanged(updateConfig, config.DragModifier != DragModifier.Ctrl,
                    config with { DragModifier = DragModifier.Ctrl }, "DragModifier changed to Ctrl");
                break;
            case IDM_DRAG_MOD_ALT:
                UpdateIfChanged(updateConfig, config.DragModifier != DragModifier.Alt,
                    config with { DragModifier = DragModifier.Alt }, "DragModifier changed to Alt");
                break;
            case IDM_DRAG_MOD_CTRL_ALT:
                UpdateIfChanged(updateConfig, config.DragModifier != DragModifier.CtrlAlt,
                    config with { DragModifier = DragModifier.CtrlAlt }, "DragModifier changed to CtrlAlt");
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
                CleanupPositions(config, currentConfig, updateConfig);
                break;

            // --- 플로팅 배지 숨김 토글 ---
            // 좌클릭 동작이 Settings/None 이라 좌클릭 토글이 막혀 있어도 숨김 해제 경로를 보장.
            case IDM_USER_HIDDEN:
                updateConfig(config with { UserHidden = !config.UserHidden });
                Logger.Info($"UserHidden toggled via menu: {!config.UserHidden}");
                break;

            // --- 커서 헤일로 숨김 토글 (메뉴 체크박스 — MF_CHECKED = 현재 숨김 상태) ---
            // 플로팅 배지 IDM_USER_HIDDEN 과 같은 패턴. 라벨 "커서 헤일로 숨김" + 체크 = 안 보임.
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

            case IDM_CURSOR_DISPLAY_SOFT:
                UpdateIfChanged(updateConfig, config.CursorDisplayMode != CursorDisplayMode.Soft,
                    config with { CursorDisplayMode = CursorDisplayMode.Soft },
                    "Cursor display mode changed to Soft");
                break;

            case IDM_CURSOR_DISPLAY_SHARP:
                UpdateIfChanged(updateConfig, config.CursorDisplayMode != CursorDisplayMode.Sharp,
                    config with { CursorDisplayMode = CursorDisplayMode.Sharp },
                    "Cursor display mode changed to Sharp");
                break;

            case IDM_CURSOR_DISPLAY_MOTION:
                UpdateIfChanged(updateConfig, config.CursorDisplayMode != CursorDisplayMode.Motion,
                    config with { CursorDisplayMode = CursorDisplayMode.Motion },
                    "Cursor display mode changed to Motion");
                break;

            // --- 상세 설정 ---
            case IDM_SETTINGS:
                // currentConfig 를 그대로 넘긴다 — 다이얼로그는 며칠이고 열려 있을 수 있고,
                // 그 사이 config.json 이 외부에서 바뀌면 커밋 베이스가 달라져야 한다 (§B).
                SettingsDialog.Show(hwndMain, config, currentConfig, updateConfig);
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

    /// <summary>현재 값과 목표가 다르면(<paramref name="changed"/>) <paramref name="updateConfig"/> 적용 +
    /// <c>Logger.Info(logMsg)</c>. PositionMode / DragModifier 등 "변경 시에만 저장 + 로그" 단순 enum 토글
    /// case 들이 공유한다. <paramref name="newConfig"/> 는 호출자가 <c>config with {…}</c> 로 합성 —
    /// changed=false 면 버려진다(record with 는 순수, 부작용 0).</summary>
    private static void UpdateIfChanged(Action<AppConfig> updateConfig, bool changed, AppConfig newConfig, string logMsg)
    {
        if (!changed) return;
        updateConfig(newConfig);
        Logger.Info(logMsg);
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
    /// 현재 활성 config.json 을 연다 (트레이 좌클릭 "설정 파일 열기").
    /// <para>
    /// <b>비 elevated</b> — 메모장. 일반 PC 에 <c>.json</c> 연결 앱이 없어 shell open 이
    /// "앱 선택"/무반응이 되기 쉽고, 메모장은 UTF-8 + hot reload 에 적합.
    /// </para>
    /// <para>
    /// <b>elevated (admin_elevation)</b> — <c>explorer.exe /select</c> 로 Medium IL 탐색기에서
    /// 파일을 연다. High IL notepad 상속(PR-03 B5 회귀, 오딧 M2)을 피한다.
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

        if (AdminElevation.IsCurrentProcessElevated())
        {
            Logger.Info("OpenConfigFile: elevated — opening via explorer /select (Medium IL)");
            UriLauncher.Open("explorer.exe", $"/select,\"{path}\"");
            return;
        }

        UriLauncher.Open("notepad.exe", $"\"{path}\"");
    }

    // ================================================================
    // Private — 기본 위치 설정
    // ================================================================

    /// <summary>
    /// 현재 플로팅 배지 위치를 가장 가까운 모서리 기준으로 환산하여 기본 위치로 저장.
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
    /// 박스가 열린 동안 감지 스레드의 배지 튐을 억제한다. RunExternal 가드를 단일 경로로
    /// 모아 호출처마다 누락되지 않도록 한다.
    /// </summary>
    private static void ShowMessage(string body)
    {
        // 표식은 반드시 **센티넬**이다 — IntPtr.Zero 를 넘기면 RunExternal 이 치환한다. 종전에는
        // _hwndMain 을 넘겼는데, 그러면 RejectReentry 가 그것을 진짜 다이얼로그로 오인해
        // **보이지 않는 0×0 메시지 창으로 포커스를 옮긴다** — 센티넬이 존재하는 이유(외부 모달은
        // 실제 창이 없으니 포커스 복원 대상에서 뺀다)가 통째로 우회됐다. 안내 박스가 떠 있는 동안
        // 사용자가 트레이 아이콘을 조작하면 그 경로를 탄다 (bug-hunt 2026-08-02 확정 #41).
        // MessageBoxW 자체의 소유자는 그대로 _hwndMain 이다 — 그건 모달 소유자 지정이라 별개다.
        ModalDialogLoop.RunExternal(IntPtr.Zero, () =>
            User32.MessageBoxW(_hwndMain, body, DefaultConfig.AppName, uType: Win32Constants.MB_OK));
    }

    private static void ShowPositionError()
        => ShowMessage(I18n.TrayPositionUnavailable);

    /// <summary>
    /// config.json 을 읽지 못해 이전 설정을 유지했음을 알린다. 호출자
    /// (<c>Program.HandleConfigChanged</c>)가 연속 실패 중 <b>1회만</b> 부른다 — 편집 중 저장이
    /// 반복되면 5초 폴링마다 박스가 떠 오히려 방해가 되기 때문 (AUDIT-2026-07-30 §G).
    /// </summary>
    internal static void ShowConfigReloadFailed()
        => ShowMessage(I18n.ConfigReloadFailed);

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
    /// 앱별 위치 기록 정리 대화상자 — empty 안내 + dialog 띄우기. 비즈니스 로직은
    /// <see cref="PositionCleanupService"/> 로 위임 (고정·창 기준 합집합, 모드 태그 라벨).
    /// </summary>
    private static void CleanupPositions(AppConfig config, Func<AppConfig> currentConfig,
        Action<AppConfig> updateConfig)
    {
        var (displayItems, originalNames) = PositionCleanupService.Compute(config);
        if (displayItems.Count == 0)
        {
            ShowMessage(I18n.TrayPositionHistoryEmpty);
            return;
        }

        List<string>? selected = CleanupDialog.Show(_hwndMain, displayItems);
        if (selected is null || selected.Count == 0) return;

        // 다이얼로그가 **자체 모달 루프**를 돌았으므로 그 사이 config 가 교체됐을 수 있다 —
        // 삭제를 적용할 베이스는 지금 값이어야 한다 (bug-hunt 2026-08-02 확정 #16).
        // displayItems/originalNames 는 사용자가 화면에서 고른 항목이라 그대로 쓴다.
        config = currentConfig();

        updateConfig(PositionCleanupService.RemoveSelected(config, displayItems, originalNames, selected));
    }
}
