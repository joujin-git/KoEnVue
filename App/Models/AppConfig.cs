using System.Text.Json;
using System.Text.Json.Serialization;
using KoEnVue.App.Config;
using KoEnVue.App.Logging;
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
    public int EventDisplayDurationMs { get; init; } = DefaultConfig.EventDisplayDurationMs;
    public int AlwaysIdleTimeoutMs { get; init; } = DefaultConfig.AlwaysIdleTimeoutMs;
    public EventTriggersConfig EventTriggers { get; init; } = new();

    // [외관 -- 스타일]
    public int LabelWidth { get; init; } = DefaultConfig.LabelWidth;
    public int LabelHeight { get; init; } = DefaultConfig.LabelHeight;
    public int LabelBorderRadius { get; init; } = DefaultConfig.LabelBorderRadius;
    public int BorderWidth { get; init; } = DefaultConfig.BorderWidth;
    public string BorderColor { get; init; } = DefaultConfig.DefaultBorderColor;

    // [외관 -- 크기 배율] — LabelWidth/Height/FontSize/BorderRadius/BorderWidth + LABEL_PADDING_X에
    // 곱해지는 배율. 트레이 메뉴에서 1.0~5.0 범위, 소수점 1자리까지 조절.
    // DPI 스케일링과 독립적으로 적용된다.
    public double IndicatorScale { get; init; } = DefaultConfig.IndicatorScale;

    // [외관 -- 색상]
    public string HangulBg { get; init; } = DefaultConfig.DefaultHangulBg;
    public string HangulFg { get; init; } = DefaultConfig.DefaultHangulFg;
    public string EnglishBg { get; init; } = DefaultConfig.DefaultEnglishBg;
    public string EnglishFg { get; init; } = DefaultConfig.DefaultEnglishFg;
    public string NonKoreanBg { get; init; } = DefaultConfig.DefaultNonKoreanBg;
    public string NonKoreanFg { get; init; } = DefaultConfig.DefaultNonKoreanFg;
    public double Opacity { get; init; } = DefaultConfig.Opacity;
    public double IdleOpacity { get; init; } = DefaultConfig.IdleOpacity;
    public double ActiveOpacity { get; init; } = DefaultConfig.ActiveOpacity;
    // [외관 -- 텍스트]
    public string FontFamily { get; init; } = DefaultConfig.DefaultIndicatorFontFamily;
    public int FontSize { get; init; } = DefaultConfig.FontSize;
    public FontWeight FontWeight { get; init; } = FontWeight.Bold;
    public string HangulLabel { get; init; } = DefaultConfig.DefaultHangulLabel;
    public string EnglishLabel { get; init; } = DefaultConfig.DefaultEnglishLabel;
    public string NonKoreanLabel { get; init; } = DefaultConfig.DefaultNonKoreanLabel;

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
    // AnimationEnabled (animation_enabled) — fade·highlight·slide 전체 마스터 (PR-22). false 면 셋 다 비활성(즉시).
    // 게이팅 합성의 단일 진실원은 App/UI/Animation.cs 의 BuildAnimationConfig (AnimationEnabled && ...).
    public bool AnimationEnabled { get; init; } = true;
    public int FadeInMs { get; init; } = DefaultConfig.FadeInMs;
    public int FadeOutMs { get; init; } = DefaultConfig.FadeOutMs;
    public bool ChangeHighlight { get; init; } = true;
    public double HighlightScale { get; init; } = DefaultConfig.HighlightScale;
    public int HighlightDurationMs { get; init; } = DefaultConfig.HighlightDurationMs;
    public bool SlideAnimation { get; init; } = true;
    public int SlideSpeedMs { get; init; } = DefaultConfig.SlideSpeedMs;

    // [동작 -- 감지]
    public int PollIntervalMs { get; init; } = DefaultConfig.PollIntervalMs;
    public DetectionMethod DetectionMethod { get; init; } = DetectionMethod.Auto;
    public NonKoreanImeMode NonKoreanIme { get; init; } = NonKoreanImeMode.Hide;
    public bool HideInFullscreen { get; init; } = true;
    public bool HideWhenNoFocus { get; init; } = true;
    public bool HideOnLockScreen { get; init; } = true;
    // DefaultConfig.DefaultSystemHideClasses 단일 진실원 참조 (AUDIT 묶음 2) — AppConfig init(MergeWithDefaults
    // 경로) 과 Settings.EnsureSubObjects 폴백(JSON 명시 null 경로) 이 같은 property 를 본다.
    public string[] SystemHideClasses { get; init; } = DefaultConfig.DefaultSystemHideClasses;
    public string[] SystemHideClassesUser { get; init; } = [];
    public string[] SystemHideProcesses { get; init; } = DefaultConfig.DefaultSystemHideProcesses;
    public string[] SystemHideProcessesUser { get; init; } = [];

    // [앱별 프로필] -- config.json 직접 편집 전용 (사용 예시: docs/User_Guide.md)
    public Dictionary<string, JsonElement> AppProfiles { get; init; } = new();
    public AppProfileMatch AppProfileMatch { get; init; } = AppProfileMatch.Process;
    public AppFilterMode AppFilterMode { get; init; } = AppFilterMode.Blacklist;
    public string[] AppFilterList { get; init; } = [];

    // [시스템 트레이]
    public bool TrayEnabled { get; init; } = true;
    public bool TrayTooltip { get; init; } = true;
    public TrayClickAction TrayClickAction { get; init; } = TrayClickAction.Toggle;
    public double[] TrayQuickOpacityPresets { get; init; } = DefaultConfig.TrayQuickOpacityPresets;

    // 트레이 좌클릭 토글로 사용자가 명시 숨김한 상태. 재기동·포그라운드 전환 시에도 유지.
    // 트레이 아이콘에는 취소선으로 시각 표시된다 (TrayIcon.DrawStrikeThrough).
    public bool UserHidden { get; init; } = false;

    // [시스템]
    [JsonConverter(typeof(LogLevelJsonConverter))]
    public LogLevel LogLevel { get; init; } = LogLevel.Info;
    public AppLanguage Language { get; init; } = AppLanguage.Auto;
    public bool LogToFile { get; init; } = true;
    public string LogFilePath { get; init; } = "";
    public int LogMaxSizeMb { get; init; } = DefaultConfig.LogMaxSizeMb;

    // [시스템 — 권한] UIPI / admin 콘솔 IME 표시. false (기본) = asInvoker 그대로 (PR-03 정책).
    // true = 단일 실행 UAC 1회 (자체 elevation) + 부팅 자동은 schtasks /RL HIGHEST (UAC 0).
    // 상세: docs/improvement-plan/PR-15-admin-elevation.md
    public bool AdminElevation { get; init; } = DefaultConfig.AdminElevation;

    // [업데이트]
    // 부팅 시 GitHub Releases API 1회 조회. 새 버전이 있으면 트레이 메뉴 최상단 헤더 라벨이
    // "KoEnVue v{cur} — GitHub" 에서 "KoEnVue v{cur} → {newTag} — 다운로드" 로 자동 전환.
    // false 로 두면 네트워크 호출 자체가 발생하지 않음 (오프라인/사내망 친화).
    public bool UpdateCheckEnabled { get; init; } = true;

    // [플로팅 배지 위치 -- 모드]
    public PositionMode PositionMode { get; init; } = PositionMode.Window;

    // [플로팅 배지 위치 -- 앱별 저장 (고정)]
    public Dictionary<string, int[]> IndicatorPositions { get; init; } = new();

    // [플로팅 배지 위치 -- 앱별 저장 (창 기준)]
    // int[3]: [(int)Corner, DeltaX, DeltaY] — 포그라운드 창 DWM 프레임 기준 상대 오프셋.
    public Dictionary<string, int[]> IndicatorPositionsRelative { get; init; } = new();

    // [플로팅 배지 위치 -- 저장 안 된 앱의 기본 표시 위치 (고정)]
    // null = 포그라운드 모니터 작업 영역 정중앙 (Overlay.ResolveWorkAreaCenter).
    // 트레이 "기본 위치 → 현재 위치로 설정" 으로 Corner+delta 를 저장하면 그 값이 우선.
    public DefaultPositionConfig? DefaultIndicatorPosition { get; init; } = null;

    // [플로팅 배지 위치 -- 저장 안 된 앱의 기본 표시 위치 (창 기준)]
    // 디폴트는 DefaultConfig.DefaultRelative* 단일 진실원 참조. 사용자가 명시적으로 null 설정 시
    // Overlay 가 동일 폴백 const 를 그대로 사용 — 두 경로 일치.
    public RelativePositionConfig? DefaultIndicatorPositionRelative { get; init; } = new()
    {
        Corner = DefaultConfig.DefaultRelativeCorner,
        DeltaX = DefaultConfig.DefaultRelativeOffsetX,
        DeltaY = DefaultConfig.DefaultRelativeOffsetY,
    };

    // [플로팅 배지 위치 -- 드래그 중 창 엣지 스냅]
    // true = 드래그 중 가시 창의 엣지와 모니터 work area 엣지에 자석처럼 붙음.
    public bool SnapToWindows { get; init; } = true;

    // 창 엣지 스냅 시 플로팅 배지와 타겟 창 사이 간격 (DPI 스케일링 전 px).
    // 0 = 엣지에 밀착, 양수 = 경계선 겹침 방지 여백. 화면 엣지에는 적용 안 됨.
    public int SnapGapPx { get; init; } = DefaultConfig.SnapGapPx;

    // [플로팅 배지 위치 -- 드래그 활성 키]
    // 드래그 승격 게이트. 짧은 좌클릭은 항상 일시 숨김. None = 임계 초과 시 드래그 승격.
    // Ctrl/Alt/CtrlAlt = 해당 키를 정확히 누른 채 임계 초과 시에만 승격 (미보유면 업 시 숨김).
    // Shift 는 드래그 중 축 고정에 선점되어 제외.
    public DragModifier DragModifier { get; init; } = DragModifier.None;

    // [커서 헤일로]
    public bool CursorIndicatorEnabled { get; init; } = DefaultConfig.CursorIndicatorEnabled;
    public bool CursorAlwaysShow { get; init; } = DefaultConfig.CursorAlwaysShow;
    public int CursorOuterRadius { get; init; } = DefaultConfig.CursorOuterRadius;
    public int CursorMiddleRadius { get; init; } = DefaultConfig.CursorMiddleRadius;
    public int CursorInnerRadius { get; init; } = DefaultConfig.CursorInnerRadius;
    public int CursorCoreThickness { get; init; } = DefaultConfig.CursorCoreThickness;
    public int CursorHaloThickness { get; init; } = DefaultConfig.CursorHaloThickness;
    public double CursorHaloOpacity { get; init; } = DefaultConfig.CursorHaloOpacity;
    public int CursorIdleDelayMs { get; init; } = DefaultConfig.CursorIdleDelayMs;
    public int CursorMotionThresholdPx { get; init; } = DefaultConfig.CursorMotionThresholdPx;
    // 전환 효과 (IME 한↔영 변경 시 스케일 팝). on/off 는 트레이 메뉴 토글 (메인 ChangeHighlight 동일 정책).
    public bool   CursorChangeHighlight     { get; init; } = DefaultConfig.CursorChangeHighlight;
    public double CursorHighlightScale       { get; init; } = DefaultConfig.CursorHighlightScale;
    public int    CursorHighlightDurationMs  { get; init; } = DefaultConfig.CursorHighlightDurationMs;
    // 커서 표시 방식 (PR-31). Soft=항상 흐릿하게(기본) / Sharp=항상 선명하게 / Motion=이동 중 흐릿하게.
    public CursorDisplayMode CursorDisplayMode { get; init; } = DefaultConfig.CursorDisplayModeDefault;
    public double CursorMotionAlpha          { get; init; } = DefaultConfig.CursorMotionAlpha;
    public double CursorMotionSoftness       { get; init; } = DefaultConfig.CursorMotionSoftness;

    // [고급]
    public AdvancedConfig Advanced { get; init; } = new();
}

// === 중첩 설정 레코드 ===

internal sealed record EventTriggersConfig
{
    public bool OnFocusChange { get; init; } = true;
    public bool OnImeChange { get; init; } = true;
}

internal sealed record AdvancedConfig
{
    public int ForceTopmostIntervalMs { get; init; } = DefaultConfig.ForceTopmostIntervalMs;
    public string OverlayClassName { get; init; } = DefaultConfig.DefaultOverlayClassName;
}

/// <summary>
/// 저장 위치가 없는 앱에 플로팅 배지를 표시할 기본 위치 (고정 모드).
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
/// 저장 위치가 없는 앱에 플로팅 배지를 표시할 기본 위치 (창 기준 모드).
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
