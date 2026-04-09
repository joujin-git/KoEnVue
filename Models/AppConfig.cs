using System.Text.Json;
using System.Text.Json.Serialization;

namespace KoEnVue.Models;

/// <summary>
/// 불변 설정 객체. volatile 참조 교체로 감지 스레드(읽기)와 메인 스레드(쓰기) 간 안전 공유.
/// 락 불필요 -- 원자적 참조 교체로 충분.
/// </summary>
internal sealed record AppConfig
{
    // [표시 모드]
    public DisplayMode DisplayMode { get; init; } = DisplayMode.Always;
    public int EventDisplayDurationMs { get; init; } = 1500;
    public int AlwaysIdleTimeoutMs { get; init; } = 3000;
    public EventTriggersConfig EventTriggers { get; init; } = new();

    // [외관 -- 스타일]
    public int LabelWidth { get; init; } = 28;
    public int LabelHeight { get; init; } = 24;
    public int LabelBorderRadius { get; init; } = 6;
    public int BorderWidth { get; init; } = 0;
    public string BorderColor { get; init; } = "#000000";

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
    public string[] SystemHideClasses { get; init; } = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    public string[] SystemHideClassesUser { get; init; } = [];

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
    public LogLevel LogLevel { get; init; } = LogLevel.Warning;
    public string Language { get; init; } = "ko";
    public bool LogToFile { get; init; } = false;
    public string LogFilePath { get; init; } = "";
    public int LogMaxSizeMb { get; init; } = 10;

    // [다중 모니터]
    public bool PerMonitorScale { get; init; } = true;
    public bool ClampToWorkArea { get; init; } = true;

    // [인디케이터 위치 -- 앱별 저장]
    public Dictionary<string, int[]> IndicatorPositions { get; init; } = new();

    // [고급]
    public AdvancedConfig Advanced { get; init; } = new();

    // [버전]
    public int ConfigVersion { get; init; } = 2;
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
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, int[]>))]
internal partial class AppConfigJsonContext : JsonSerializerContext { }
// SnakeCaseLower: C# PascalCase 프로퍼티 ↔ JSON snake_case 키 자동 매핑
// 예: FadeInMs ↔ "fade_in_ms", HangulBg ↔ "hangul_bg"
