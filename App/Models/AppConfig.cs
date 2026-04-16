using System.Text.Json;
using System.Text.Json.Serialization;
using KoEnVue.Core.Logging;

namespace KoEnVue.App.Models;

/// <summary>
/// 불변 설정 객체. volatile 참조 교체로 감지 스레드(읽기)와 메인 스레드(쓰기) 간 안전 공유.
/// 락 불필요 -- 원자적 참조 교체로 충분.
/// </summary>
internal sealed record AppConfig
{
    // [표시 모드]
    public DisplayMode DisplayMode { get; init; } = DisplayMode.Always;
    public int EventDisplayDurationMs { get; init; } = 2000;
    public int AlwaysIdleTimeoutMs { get; init; } = 3000;
    public EventTriggersConfig EventTriggers { get; init; } = new();

    // [외관 -- 스타일]
    public int LabelWidth { get; init; } = 28;
    public int LabelHeight { get; init; } = 24;
    public int LabelBorderRadius { get; init; } = 6;
    public int BorderWidth { get; init; } = 0;
    public string BorderColor { get; init; } = "#000000";

    // [외관 -- 크기 배율] — LabelWidth/Height/FontSize/BorderRadius/BorderWidth + LABEL_PADDING_X에
    // 곱해지는 배율. 트레이 메뉴에서 1.0~5.0 범위, 소수점 1자리까지 조절.
    // DPI 스케일링과 독립적으로 적용된다.
    public double IndicatorScale { get; init; } = 1.0;

    // [외관 -- 색상]
    public string HangulBg { get; init; } = "#16A34A";
    public string HangulFg { get; init; } = "#FFFFFF";
    public string EnglishBg { get; init; } = "#D97706";
    public string EnglishFg { get; init; } = "#FFFFFF";
    public string NonKoreanBg { get; init; } = "#6B7280";
    public string NonKoreanFg { get; init; } = "#FFFFFF";
    public double Opacity { get; init; } = 0.85;
    public double IdleOpacity { get; init; } = 0.4;
    public double ActiveOpacity { get; init; } = 0.95;
    // [외관 -- 텍스트]
    public string FontFamily { get; init; } = "맑은 고딕";
    public int FontSize { get; init; } = 12;
    public FontWeight FontWeight { get; init; } = FontWeight.Bold;
    public string HangulLabel { get; init; } = "한";
    public string EnglishLabel { get; init; } = "En";
    public string NonKoreanLabel { get; init; } = "EN";

    // [외관 -- 테마]
    public Theme Theme { get; init; } = Theme.Custom;

    // [외관 -- 테마 백업] — 프리셋 적용 전 커스텀 색상 보존. 프리셋 활성 시에만 값 존재.
    public string? CustomBackupHangulBg { get; init; }
    public string? CustomBackupHangulFg { get; init; }
    public string? CustomBackupEnglishBg { get; init; }
    public string? CustomBackupEnglishFg { get; init; }
    public string? CustomBackupNonKoreanBg { get; init; }
    public string? CustomBackupNonKoreanFg { get; init; }

    // [애니메이션]
    public bool AnimationEnabled { get; init; } = true;
    public int FadeInMs { get; init; } = 150;
    public int FadeOutMs { get; init; } = 400;
    public bool ChangeHighlight { get; init; } = true;
    public double HighlightScale { get; init; } = 1.3;
    public int HighlightDurationMs { get; init; } = 300;
    public bool SlideAnimation { get; init; } = false;
    public int SlideSpeedMs { get; init; } = 100;

    // [동작 -- 감지]
    public int PollIntervalMs { get; init; } = 80;
    public DetectionMethod DetectionMethod { get; init; } = DetectionMethod.Auto;
    public NonKoreanImeMode NonKoreanIme { get; init; } = NonKoreanImeMode.Hide;
    public bool HideInFullscreen { get; init; } = true;
    public bool HideWhenNoFocus { get; init; } = true;
    public bool HideOnLockScreen { get; init; } = true;
    public string[] SystemHideClasses { get; init; } = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow_WASDK"];
    public string[] SystemHideClassesUser { get; init; } = [];
    public string[] SystemHideProcesses { get; init; } = ["ShellExperienceHost"];
    public string[] SystemHideProcessesUser { get; init; } = [];

    // [앱별 프로필] -- Phase 06에서 구현
    public Dictionary<string, JsonElement> AppProfiles { get; init; } = new();
    public AppProfileMatch AppProfileMatch { get; init; } = AppProfileMatch.Process;
    public AppFilterMode AppFilterMode { get; init; } = AppFilterMode.Blacklist;
    public string[] AppFilterList { get; init; } = [];

    // [핫키]
    public bool HotkeysEnabled { get; init; } = true;
    public string HotkeyToggleVisibility { get; init; } = "Ctrl+Alt+H";

    // [시스템 트레이]
    public bool TrayEnabled { get; init; } = true;
    public TrayIconStyle TrayIconStyle { get; init; } = TrayIconStyle.CaretDot;
    public bool TrayTooltip { get; init; } = true;
    public TrayClickAction TrayClickAction { get; init; } = TrayClickAction.Toggle;
    public bool TrayShowNotification { get; init; } = false;
    public double[] TrayQuickOpacityPresets { get; init; } = [0.95, 0.85, 0.6];

    // [시스템]
    public bool StartupWithWindows { get; init; } = false;
    public bool StartupMinimized { get; init; } = true;
    public bool SingleInstance { get; init; } = true;
    public LogLevel LogLevel { get; init; } = LogLevel.Info;
    public string Language { get; init; } = "auto";
    public bool LogToFile { get; init; } = true;
    public string LogFilePath { get; init; } = "";
    public int LogMaxSizeMb { get; init; } = 10;

    // [업데이트]
    // 부팅 시 GitHub Releases API 1회 조회. 새 버전이 있으면 트레이 메뉴 상단에 "새 버전 있음 ..." 항목 노출.
    // false 로 두면 네트워크 호출 자체가 발생하지 않음 (오프라인/사내망 친화).
    public bool UpdateCheckEnabled { get; init; } = true;

    // [다중 모니터]
    public bool PerMonitorScale { get; init; } = true;
    public bool ClampToWorkArea { get; init; } = true;

    // [인디케이터 위치 -- 모드]
    public PositionMode PositionMode { get; init; } = PositionMode.Fixed;

    // [인디케이터 위치 -- 앱별 저장 (고정)]
    public Dictionary<string, int[]> IndicatorPositions { get; init; } = new();

    // [인디케이터 위치 -- 앱별 저장 (창 기준)]
    // int[3]: [(int)Corner, DeltaX, DeltaY] — 포그라운드 창 DWM 프레임 기준 상대 오프셋.
    public Dictionary<string, int[]> IndicatorPositionsRelative { get; init; } = new();

    // [인디케이터 위치 -- 저장 안 된 앱의 기본 표시 위치 (고정)]
    // null = 하드코딩 폴백 (work area 우상단, DefaultConfig.DefaultIndicatorOffset*).
    // 값이 있으면 Corner anchor + delta로 포그라운드 모니터 work area 기준 위치 계산.
    public DefaultPositionConfig? DefaultIndicatorPosition { get; init; } = null;

    // [인디케이터 위치 -- 저장 안 된 앱의 기본 표시 위치 (창 기준)]
    // null = 하드코딩 폴백 (창 TopRight, DefaultConfig.DefaultRelativeOffset*).
    // 값이 있으면 Corner anchor + delta로 포그라운드 창 DWM 프레임 기준 위치 계산.
    public RelativePositionConfig? DefaultIndicatorPositionRelative { get; init; } = null;

    // [인디케이터 위치 -- 드래그 중 창 엣지 스냅]
    // true = 드래그 중 가시 창의 엣지와 모니터 work area 엣지에 자석처럼 붙음.
    public bool SnapToWindows { get; init; } = true;

    // 창 엣지 스냅 시 인디케이터와 타겟 창 사이 간격 (DPI 스케일링 전 px).
    // 0 = 엣지에 밀착, 양수 = 경계선 겹침 방지 여백. 화면 엣지에는 적용 안 됨.
    public int SnapGapPx { get; init; } = 2;

    // [고급]
    public AdvancedConfig Advanced { get; init; } = new();

    // [버전]
    public int ConfigVersion { get; init; } = 3;
}

// === 중첩 설정 레코드 ===

internal sealed record EventTriggersConfig
{
    public bool OnFocusChange { get; init; } = true;
    public bool OnImeChange { get; init; } = true;
}

internal sealed record AdvancedConfig
{
    public int ForceTopmostIntervalMs { get; init; } = 5000;
    public string[] ImeFallbackChain { get; init; } = ["ime_default_wnd", "ime_context", "keyboard_layout"];
    public string OverlayClassName { get; init; } = "KoEnVueOverlay";
    public bool PreventSleep { get; init; } = false;
}

/// <summary>
/// 저장 위치가 없는 앱에 인디케이터를 표시할 기본 위치 (고정 모드).
/// Corner anchor + delta — 포그라운드 모니터 work area 기준으로 계산되므로
/// 멀티모니터·해상도 변경에 안정적.
/// </summary>
internal sealed record DefaultPositionConfig
{
    public Corner Corner { get; init; } = Corner.TopRight;
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
}

/// <summary>
/// 저장 위치가 없는 앱에 인디케이터를 표시할 기본 위치 (창 기준 모드).
/// Corner anchor + delta — 포그라운드 창의 DWM visible frame 기준.
/// </summary>
internal sealed record RelativePositionConfig
{
    public Corner Corner { get; init; } = Corner.TopRight;
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }
}

/// <summary>
/// NativeAOT 필수: JsonSerializerContext source generator.
/// 리플렉션 비활성화 상태에서 직렬화/역직렬화를 수행.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(EventTriggersConfig))]
[JsonSerializable(typeof(AdvancedConfig))]
[JsonSerializable(typeof(DefaultPositionConfig))]
[JsonSerializable(typeof(RelativePositionConfig))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, int[]>))]
internal partial class AppConfigJsonContext : JsonSerializerContext { }
// SnakeCaseLower: C# PascalCase 프로퍼티 ↔ JSON snake_case 키 자동 매핑
// 예: FadeInMs ↔ "fade_in_ms", HangulBg ↔ "hangul_bg"
