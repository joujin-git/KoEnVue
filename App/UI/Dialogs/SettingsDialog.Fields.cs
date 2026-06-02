using System.Globalization;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Logging;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// SettingsDialog 의 필드 정의/팩토리/빌더 분할.
/// FieldDef/RowDef 메타데이터, 6개 팩토리(Bool/Int/Dbl/Str/ColorField/Combo),
/// BuildRowDefs(14 섹션 — 일반/공용 색상/메인·커서 인디케이터 대분류), 헬퍼(ReadEdit/GetPresetAt/SetPresetAt/언어 매핑).
/// </summary>
internal static partial class SettingsDialog
{
    // ================================================================
    // 필드 읽기/표시 상수
    // ================================================================

    /// <summary>EDIT 텍스트 읽기 버퍼의 길이 슬랙 (char). 현재 텍스트 길이에 더해 여유분 확보.</summary>
    private const int EditReadBufferSlackChars = 2;

    /// <summary>Double 필드 값 표시 포맷 — EDIT 박스 초기값용 (소수 셋째 자리까지).</summary>
    private const string DoubleFieldDisplayFormat = "0.###";

    /// <summary>Double 범위 초과 안내의 min/max 표시 포맷 — 경계값용 (소수 둘째 자리까지, 표시 의도 분리).</summary>
    private const string DoubleRangeBoundFormat = "0.##";

    // ================================================================
    // 필드 메타데이터
    // ================================================================

    private enum FieldType { Bool, Int, Double, String, Color, Combo }

    private sealed class FieldDef
    {
        public required FieldType Type { get; init; }
        public required string LabelKo { get; init; }
        public required string LabelEn { get; init; }
        public string Label => I18n.IsKorean ? LabelKo : LabelEn;

        public Func<AppConfig, bool>? GetBool { get; init; }
        public Func<AppConfig, string>? GetString { get; init; }
        public Func<AppConfig, int>? GetEnumIndex { get; init; }

        public required Func<AppConfig, IntPtr, string, (AppConfig? Config, string? Error)> Commit { get; init; }

        public string[]? EnumLabels { get; init; }
    }

    private sealed class RowDef
    {
        public bool IsSection { get; init; }
        public string? SectionKo { get; init; }
        public string? SectionEn { get; init; }
        public FieldDef? Field { get; init; }
        public string SectionLabel => I18n.IsKorean ? (SectionKo ?? "") : (SectionEn ?? "");
    }

    // ================================================================
    // 필드/컨트롤 추적 (BuildRowDefs 빌드 + Show 의 컨트롤 생성에서 채워짐)
    // ================================================================

    private static readonly List<FieldDef> _fields = new();
    private static readonly List<IntPtr> _fieldInputs = new();  // _fields 와 같은 순서·같은 길이

    // ================================================================
    // 필드 정의 빌더
    // ================================================================

    /// <summary>
    /// 14개 섹션(일반/공용 색상/메인·커서 인디케이터 대분류)의 RowDef 리스트를 빌드하며 _fields를 채운다.
    /// 각 섹션 제목, 라벨, 검증 범위는 언어(I18n.IsKorean)에 따라 결정된다.
    /// </summary>
    private static List<RowDef> BuildRowDefs()
    {
        _fields.Clear();
        var rows = new List<RowDef>();
        bool ko = I18n.IsKorean;

        void Sec(string sko, string sen)
            => rows.Add(new RowDef { IsSection = true, SectionKo = sko, SectionEn = sen });

        void Add(FieldDef f)
        {
            _fields.Add(f);
            rows.Add(new RowDef { IsSection = false, Field = f });
        }

        // 섹션은 일반 / 공용 색상 / 메인 인디케이터 / 커서 인디케이터 로 묶고 고급은 말미에 둔다. 각 FieldDef 의
        // get/Commit 람다는 독립적이라 순서가 바뀌어도 정상 — _fields 인덱스와 컨트롤 짝만 Add 가 보장.

        // ================================================================
        // 1. 일반 (인디 종류 무관 앱 전역 설정)
        // ================================================================
        Sec("일반", "General");
        Add(Combo("언어", "Language",
            ko ? ["자동", "한국어", "English"] : ["Auto", "Korean", "English"],
            c => (int)c.Language,
            (c, i) => c with { Language = (AppLanguage)Math.Clamp(i, 0, 2) }));
        Add(Combo("로그 레벨", "Log level",
            ko ? ["디버그", "정보", "경고", "오류"] : ["Debug", "Info", "Warning", "Error"],
            c => (int)c.LogLevel,
            (c, i) => c with { LogLevel = (LogLevel)Math.Clamp(i, 0, 3) }));
        Add(Bool("파일에 로그 기록", "Log to file",
            c => c.LogToFile, (c, v) => c with { LogToFile = v }));
        Add(Str("로그 파일 경로", "Log file path",
            c => c.LogFilePath, (c, v) => c with { LogFilePath = v }, allowEmpty: true));
        Add(Int("로그 최대 크기 (MB)", "Log max size (MB)",
            DefaultConfig.MinLogMaxSizeMb, DefaultConfig.MaxLogMaxSizeMb,
            c => c.LogMaxSizeMb, (c, v) => c with { LogMaxSizeMb = v }));
        Add(Bool("부팅 시 업데이트 확인", "Check for updates on startup",
            c => c.UpdateCheckEnabled, (c, v) => c with { UpdateCheckEnabled = v }));
        // PR-15: admin_elevation — UIPI 우회 (admin 콘솔 한/영 표시). Settings 토글은 다음 부팅 적용
        // (SyncStartupPathAsync 가 자동 재등록). 즉시 적용 원하면 트레이 메뉴 사용 — 결정 #4 (Settings 사일런트).
        Add(Bool("관리자 권한으로 실행", "Run as administrator",
            c => c.AdminElevation, (c, v) => c with { AdminElevation = v }));

        // ================================================================
        // 2. 일반 — 트레이
        // ================================================================
        Sec("일반 — 트레이", "General — Tray");
        Add(Bool("툴팁 표시", "Show tooltip",
            c => c.TrayTooltip, (c, v) => c with { TrayTooltip = v }));
        Add(Combo("좌클릭 동작", "Left-click action",
            ko ? ["표시 토글", "설정 파일 열기", "동작 없음"]
               : ["Toggle visibility", "Open config file", "None"],
            c => (int)c.TrayClickAction,
            (c, i) => c with { TrayClickAction = (TrayClickAction)Math.Clamp(i, 0, 2) }));
        Add(Dbl("빠른 투명도 1 (진하게)", "Quick opacity 1 (High)",
            DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 0, DefaultConfig.TrayQuickOpacity1),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 0, v) }));
        Add(Dbl("빠른 투명도 2 (보통)", "Quick opacity 2 (Normal)",
            DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 1, DefaultConfig.TrayQuickOpacity2),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 1, v) }));
        Add(Dbl("빠른 투명도 3 (연하게)", "Quick opacity 3 (Low)",
            DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 2, DefaultConfig.TrayQuickOpacity3),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 2, v) }));

        // ================================================================
        // 3. 인디케이터 — 색상 (메인/커서 인디케이터 공통)
        // ================================================================
        // 배경색은 CursorOverlay.BuildStyle 이 커서 동심원 색으로도 그대로 사용한다 (메인·커서 공용).
        // 테마는 이 배경색(+글자색)을 일괄 지정/복원하는 프리셋이라 같은 섹션에 둔다. 메인·커서 공용이라
        // 인디케이터 섹션들 맨 앞에 배치.
        Sec("인디케이터 — 색상 (메인/커서 인디케이터 공통)", "Indicator — Colors (Shared by Main/Cursor Indicators)");
        Add(Combo("테마", "Theme",
            ko ? ["사용자 지정", "미니멀", "비비드", "파스텔", "다크", "시스템"]
               : ["Custom", "Minimal", "Vivid", "Pastel", "Dark", "System"],
            c => (int)c.Theme,
            (c, i) => c with { Theme = (Theme)Math.Clamp(i, 0, 5) }));
        Add(ColorField("한글 배경색", "Hangul background",
            c => c.HangulBg, (c, v) => c with { HangulBg = v }));
        Add(ColorField("영문 배경색", "English background",
            c => c.EnglishBg, (c, v) => c with { EnglishBg = v }));
        Add(ColorField("비한국어 배경색", "Non-Korean background",
            c => c.NonKoreanBg, (c, v) => c with { NonKoreanBg = v }));

        // ================================================================
        // 4. 메인 인디케이터 — 표시 모드
        // ================================================================
        Sec("메인 인디케이터 — 표시 모드", "Main — Display Mode");
        Add(Combo("표시 방식", "Display mode",
            ko ? ["이벤트 시", "항상"] : ["On Event", "Always"],
            c => (int)c.DisplayMode,
            (c, i) => c with { DisplayMode = (DisplayMode)Math.Clamp(i, 0, 1) }));
        Add(Int("이벤트 표시 시간 (ms)", "Event display duration (ms)",
            DefaultConfig.MinEventDisplayMs, DefaultConfig.MaxEventDisplayMs,
            c => c.EventDisplayDurationMs,
            (c, v) => c with { EventDisplayDurationMs = v }));
        Add(Int("항상 모드 유휴 전환 (ms)", "Always-mode idle timeout (ms)",
            DefaultConfig.MinAlwaysIdleMs, DefaultConfig.MaxAlwaysIdleMs,
            c => c.AlwaysIdleTimeoutMs,
            (c, v) => c with { AlwaysIdleTimeoutMs = v }));
        Add(Bool("포커스 변경 시 이벤트", "Trigger on focus change",
            c => c.EventTriggers.OnFocusChange,
            (c, v) => c with { EventTriggers = c.EventTriggers with { OnFocusChange = v } }));
        Add(Bool("IME 변경 시 이벤트", "Trigger on IME change",
            c => c.EventTriggers.OnImeChange,
            (c, v) => c with { EventTriggers = c.EventTriggers with { OnImeChange = v } }));

        // ================================================================
        // 5. 메인 인디케이터 — 크기·테두리
        // ================================================================
        Sec("메인 인디케이터 — 크기·테두리", "Main — Size & Border");
        Add(Int("라벨 너비 (px)", "Label width (px)",
            DefaultConfig.MinLabelWidth, DefaultConfig.MaxLabelWidth,
            c => c.LabelWidth, (c, v) => c with { LabelWidth = v }));
        Add(Int("라벨 높이 (px)", "Label height (px)",
            DefaultConfig.MinLabelHeight, DefaultConfig.MaxLabelHeight,
            c => c.LabelHeight, (c, v) => c with { LabelHeight = v }));
        Add(Int("테두리 둥글기 (px)", "Border radius (px)",
            DefaultConfig.MinLabelBorderRadius, DefaultConfig.MaxLabelBorderRadius,
            c => c.LabelBorderRadius, (c, v) => c with { LabelBorderRadius = v }));
        Add(Int("테두리 두께 (px)", "Border width (px)",
            DefaultConfig.MinBorderWidth, DefaultConfig.MaxBorderWidth,
            c => c.BorderWidth, (c, v) => c with { BorderWidth = v }));
        Add(ColorField("테두리 색상", "Border color",
            c => c.BorderColor, (c, v) => c with { BorderColor = v }));

        // ================================================================
        // 6. 메인 인디케이터 — 글자색·투명도
        // ================================================================
        // 글자색은 메인 라벨 텍스트 전용 (커서는 글자가 없어 헤일로 흰색 고정). 투명도도 메인 전용
        // (커서 본체는 항상 불투명, 헤일로 투명도는 동심원 섹션의 CursorHaloOpacity 별도).
        Sec("메인 인디케이터 — 글자색·투명도", "Main — Foreground & Opacity");
        Add(ColorField("한글 글자색", "Hangul foreground",
            c => c.HangulFg, (c, v) => c with { HangulFg = v }));
        Add(ColorField("영문 글자색", "English foreground",
            c => c.EnglishFg, (c, v) => c with { EnglishFg = v }));
        Add(ColorField("비한국어 글자색", "Non-Korean foreground",
            c => c.NonKoreanFg, (c, v) => c with { NonKoreanFg = v }));
        Add(Dbl("유휴 투명도", "Idle opacity",
            DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity,
            c => c.IdleOpacity, (c, v) => c with { IdleOpacity = v }));
        Add(Dbl("활성 투명도", "Active opacity",
            DefaultConfig.MinOpacity, DefaultConfig.MaxOpacity,
            c => c.ActiveOpacity, (c, v) => c with { ActiveOpacity = v }));

        // ================================================================
        // 7. 메인 인디케이터 — 텍스트
        // ================================================================
        Sec("메인 인디케이터 — 텍스트", "Main — Text");
        Add(Str("글꼴", "Font family",
            c => c.FontFamily, (c, v) => c with { FontFamily = v }, allowEmpty: false));
        Add(Int("글꼴 크기", "Font size",
            DefaultConfig.MinFontSize, DefaultConfig.MaxFontSize,
            c => c.FontSize, (c, v) => c with { FontSize = v }));
        Add(Combo("글꼴 굵기", "Font weight",
            ko ? ["보통", "굵게"] : ["Normal", "Bold"],
            c => (int)c.FontWeight,
            (c, i) => c with { FontWeight = (FontWeight)Math.Clamp(i, 0, 1) }));
        Add(Str("한글 라벨", "Hangul label",
            c => c.HangulLabel, (c, v) => c with { HangulLabel = v }, allowEmpty: false));
        Add(Str("영문 라벨", "English label",
            c => c.EnglishLabel, (c, v) => c with { EnglishLabel = v }, allowEmpty: false));
        Add(Str("비한국어 라벨", "Non-Korean label",
            c => c.NonKoreanLabel, (c, v) => c with { NonKoreanLabel = v }, allowEmpty: false));

        // ================================================================
        // 8. 메인 인디케이터 — 애니메이션
        // ================================================================
        // AnimationEnabled / ChangeHighlight는 트레이 메뉴에서 토글 가능하므로 여기서 제외.
        Sec("메인 인디케이터 — 애니메이션", "Main — Animation");
        Add(Int("페이드 인 (ms)", "Fade in (ms)",
            DefaultConfig.MinFadeMs, DefaultConfig.MaxFadeMs,
            c => c.FadeInMs, (c, v) => c with { FadeInMs = v }));
        Add(Int("페이드 아웃 (ms)", "Fade out (ms)",
            DefaultConfig.MinFadeMs, DefaultConfig.MaxFadeMs,
            c => c.FadeOutMs, (c, v) => c with { FadeOutMs = v }));
        Add(Dbl("강조 배율", "Highlight scale",
            DefaultConfig.MinHighlightScale, DefaultConfig.MaxHighlightScale,
            c => c.HighlightScale, (c, v) => c with { HighlightScale = v }));
        Add(Int("강조 지속 시간 (ms)", "Highlight duration (ms)",
            DefaultConfig.MinFadeMs, DefaultConfig.MaxFadeMs,
            c => c.HighlightDurationMs, (c, v) => c with { HighlightDurationMs = v }));
        Add(Bool("슬라이드 애니메이션", "Slide animation",
            c => c.SlideAnimation, (c, v) => c with { SlideAnimation = v }));
        Add(Int("슬라이드 속도 (ms)", "Slide speed (ms)",
            DefaultConfig.MinFadeMs, DefaultConfig.MaxFadeMs,
            c => c.SlideSpeedMs, (c, v) => c with { SlideSpeedMs = v }));

        // ================================================================
        // 9. 메인 인디케이터 — 감지·숨김
        // ================================================================
        Sec("메인 인디케이터 — 감지·숨김", "Main — Detection & Hiding");
        Add(Int("감지 주기 (ms)", "Poll interval (ms)",
            DefaultConfig.MinPollMs, DefaultConfig.MaxPollMs,
            c => c.PollIntervalMs, (c, v) => c with { PollIntervalMs = v }));
        Add(Combo("감지 방식", "Detection method",
            ko ? ["자동", "IME 기본 윈도우", "IME 컨텍스트", "키보드 레이아웃"]
               : ["Auto", "IME default", "IME context", "Keyboard layout"],
            c => (int)c.DetectionMethod,
            (c, i) => c with { DetectionMethod = (DetectionMethod)Math.Clamp(i, 0, 3) }));
        Add(Combo("비한국어 IME 처리", "Non-Korean IME mode",
            ko ? ["숨김", "표시", "어둡게"] : ["Hide", "Show", "Dim"],
            c => (int)c.NonKoreanIme,
            (c, i) => c with { NonKoreanIme = (NonKoreanImeMode)Math.Clamp(i, 0, 2) }));
        Add(Bool("전체화면에서 숨기기", "Hide in fullscreen",
            c => c.HideInFullscreen, (c, v) => c with { HideInFullscreen = v }));
        Add(Bool("포커스 없을 때 숨기기", "Hide when no focus",
            c => c.HideWhenNoFocus, (c, v) => c with { HideWhenNoFocus = v }));
        Add(Bool("잠금 화면에서 숨기기", "Hide on lock screen",
            c => c.HideOnLockScreen, (c, v) => c with { HideOnLockScreen = v }));

        // ================================================================
        // 10. 메인 인디케이터 — 앱별 프로필
        // ================================================================
        Sec("메인 인디케이터 — 앱별 프로필", "Main — App Profiles");
        Add(Combo("매칭 기준", "Match by",
            ko ? ["프로세스", "윈도우 클래스", "윈도우 타이틀"]
               : ["Process", "Window class", "Window title"],
            c => (int)c.AppProfileMatch,
            (c, i) => c with { AppProfileMatch = (AppProfileMatch)Math.Clamp(i, 0, 2) }));
        Add(Combo("필터 모드", "Filter mode",
            ko ? ["블랙리스트 (목록 숨김)", "화이트리스트 (목록만 표시)"]
               : ["Blacklist (hide listed)", "Whitelist (show only listed)"],
            c => (int)c.AppFilterMode,
            (c, i) => c with { AppFilterMode = (AppFilterMode)Math.Clamp(i, 0, 1) }));

        // ================================================================
        // 11. 메인 인디케이터 — 조작
        // ================================================================
        Sec("메인 인디케이터 — 조작", "Main — Interaction");
        Add(Int("창 스냅 간격 (px)", "Snap gap (px)",
            DefaultConfig.MinSnapGapPx, DefaultConfig.MaxSnapGapPx,
            c => c.SnapGapPx, (c, v) => c with { SnapGapPx = v }));
        Add(Combo("드래그 활성 키", "Drag modifier",
            ko ? ["없음", "Ctrl", "Alt", "Ctrl + Alt"]
               : ["None", "Ctrl", "Alt", "Ctrl + Alt"],
            c => (int)c.DragModifier,
            (c, i) => c with { DragModifier = (DragModifier)Math.Clamp(i, 0, 3) }));

        // ================================================================
        // 12. 커서 인디케이터 — 동심원
        // ================================================================
        Sec("커서 인디케이터 — 동심원", "Cursor — Rings");
        Add(Bool("커서 인디케이터 사용", "Enable cursor indicator",
            c => c.CursorIndicatorEnabled, (c, v) => c with { CursorIndicatorEnabled = v }));
        Add(Bool("항상 표시 (마우스 추종)", "Always show (follow cursor)",
            c => c.CursorAlwaysShow, (c, v) => c with { CursorAlwaysShow = v }));
        Add(Int("외부 원 반지름 (px)", "Outer radius (px)",
            DefaultConfig.MinCursorOuterRadius, DefaultConfig.MaxCursorOuterRadius,
            c => c.CursorOuterRadius, (c, v) => c with { CursorOuterRadius = v }));
        Add(Int("중간 원 반지름 (px)", "Middle radius (px)",
            DefaultConfig.MinCursorMiddleRadius, DefaultConfig.MaxCursorMiddleRadius,
            c => c.CursorMiddleRadius, (c, v) => c with { CursorMiddleRadius = v }));
        Add(Int("내부 원 반지름 (px)", "Inner radius (px)",
            DefaultConfig.MinCursorInnerRadius, DefaultConfig.MaxCursorInnerRadius,
            c => c.CursorInnerRadius, (c, v) => c with { CursorInnerRadius = v }));
        Add(Int("코어 두께 (px)", "Core thickness (px)",
            DefaultConfig.MinCursorCoreThickness, DefaultConfig.MaxCursorCoreThickness,
            c => c.CursorCoreThickness, (c, v) => c with { CursorCoreThickness = v }));
        Add(Int("헤일로 두께 (px)", "Halo thickness (px)",
            DefaultConfig.MinCursorHaloThickness, DefaultConfig.MaxCursorHaloThickness,
            c => c.CursorHaloThickness, (c, v) => c with { CursorHaloThickness = v }));
        Add(Dbl("헤일로 불투명도", "Halo opacity",
            DefaultConfig.MinCursorHaloOpacity, DefaultConfig.MaxCursorHaloOpacity,
            c => c.CursorHaloOpacity, (c, v) => c with { CursorHaloOpacity = v }));
        Add(Int("유휴 전환 지연 (ms)", "Idle delay (ms)",
            DefaultConfig.MinCursorIdleDelayMs, DefaultConfig.MaxCursorIdleDelayMs,
            c => c.CursorIdleDelayMs, (c, v) => c with { CursorIdleDelayMs = v }));
        Add(Int("이동 임계값 (px)", "Motion threshold (px)",
            DefaultConfig.MinCursorMotionThresholdPx, DefaultConfig.MaxCursorMotionThresholdPx,
            c => c.CursorMotionThresholdPx, (c, v) => c with { CursorMotionThresholdPx = v }));

        // ================================================================
        // 13. 커서 인디케이터 — 전환 효과
        // ================================================================
        // CursorChangeHighlight on/off 는 트레이 메뉴 토글이라 여기서 제외 (메인 ChangeHighlight 패턴 동일).
        Sec("커서 인디케이터 — 전환 효과", "Cursor — Transition");
        Add(Dbl("전환 강조 배율", "Highlight scale",
            DefaultConfig.MinCursorHighlightScale, DefaultConfig.MaxCursorHighlightScale,
            c => c.CursorHighlightScale, (c, v) => c with { CursorHighlightScale = v }));
        Add(Int("전환 강조 지속 시간 (ms)", "Highlight duration (ms)",
            DefaultConfig.MinCursorHighlightDurationMs, DefaultConfig.MaxCursorHighlightDurationMs,
            c => c.CursorHighlightDurationMs, (c, v) => c with { CursorHighlightDurationMs = v }));

        // ================================================================
        // 14. 고급
        // ================================================================
        Sec("고급", "Advanced");
        Add(Int("TOPMOST 강제 주기 (ms)", "Force topmost interval (ms)",
            DefaultConfig.MinForceTopmostMs, DefaultConfig.MaxForceTopmostMs,
            c => c.Advanced.ForceTopmostIntervalMs,
            (c, v) => c with { Advanced = c.Advanced with { ForceTopmostIntervalMs = v } }));

        return rows;
    }

    // ================================================================
    // FieldDef 팩토리
    // ================================================================

    private static FieldDef Bool(string ko, string en,
        Func<AppConfig, bool> get, Func<AppConfig, bool, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Bool,
            LabelKo = ko,
            LabelEn = en,
            GetBool = get,
            Commit = (cfg, hwnd, _) =>
            {
                IntPtr state = User32.SendMessageW(hwnd, Win32Constants.BM_GETCHECK,
                    IntPtr.Zero, IntPtr.Zero);
                bool v = state == (IntPtr)Win32Constants.BST_CHECKED;
                return (set(cfg, v), null);
            },
        };
    }

    private static FieldDef Int(string ko, string en, int min, int max,
        Func<AppConfig, int> get, Func<AppConfig, int, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Int,
            LabelKo = ko,
            LabelEn = en,
            GetString = cfg => get(cfg).ToString(CultureInfo.InvariantCulture),
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidNumberFmt, label));
                if (v < min || v > max)
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsOutOfRangeFmt, label, min, max));
                return (set(cfg, v), null);
            },
        };
    }

    private static FieldDef Dbl(string ko, string en, double min, double max,
        Func<AppConfig, double> get, Func<AppConfig, double, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Double,
            LabelKo = ko,
            LabelEn = en,
            GetString = cfg => get(cfg).ToString(DoubleFieldDisplayFormat, CultureInfo.InvariantCulture),
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidNumberFmt, label));
                if (v < min || v > max)
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsOutOfRangeFmt, label,
                        min.ToString(DoubleRangeBoundFormat, CultureInfo.InvariantCulture),
                        max.ToString(DoubleRangeBoundFormat, CultureInfo.InvariantCulture)));
                return (set(cfg, v), null);
            },
        };
    }

    private static FieldDef Str(string ko, string en,
        Func<AppConfig, string> get, Func<AppConfig, string, AppConfig> set, bool allowEmpty)
    {
        return new FieldDef
        {
            Type = FieldType.String,
            LabelKo = ko,
            LabelEn = en,
            GetString = get,
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!allowEmpty && string.IsNullOrWhiteSpace(text))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsEmptyNotAllowedFmt, label));
                return (set(cfg, text), null);
            },
        };
    }

    private static FieldDef ColorField(string ko, string en,
        Func<AppConfig, string> get, Func<AppConfig, string, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Color,
            LabelKo = ko,
            LabelEn = en,
            GetString = get,
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!ColorHelper.TryNormalizeHex(text, out string normalized))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidColorFmt, label));
                return (set(cfg, normalized), null);
            },
        };
    }

    private static FieldDef Combo(string ko, string en, string[] labels,
        Func<AppConfig, int> getIdx, Func<AppConfig, int, AppConfig> setIdx)
    {
        return new FieldDef
        {
            Type = FieldType.Combo,
            LabelKo = ko,
            LabelEn = en,
            EnumLabels = labels,
            GetEnumIndex = getIdx,
            Commit = (cfg, hwnd, _) =>
            {
                IntPtr sel = User32.SendMessageW(hwnd, Win32Constants.CB_GETCURSEL,
                    IntPtr.Zero, IntPtr.Zero);
                int idx = (int)sel.ToInt64();
                if (idx < 0) idx = 0;
                if (idx >= labels.Length) idx = labels.Length - 1;
                return (setIdx(cfg, idx), null);
            },
        };
    }

    // ================================================================
    // 헬퍼
    // ================================================================

    private static string ReadEdit(IntPtr hwnd)
    {
        int len = User32.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        char[] buf = new char[len + EditReadBufferSlackChars];
        int read = User32.GetWindowTextW(hwnd, buf, buf.Length);
        return read > 0 ? new string(buf, 0, read).Trim() : "";
    }

    /// <summary>
    /// TrayQuickOpacityPresets 배열의 i번째 값을 안전하게 읽는다 (배열 길이 부족 시 fallback).
    /// </summary>
    private static double GetPresetAt(double[] presets, int i, double fallback)
        => presets != null && i >= 0 && i < presets.Length ? presets[i] : fallback;

    /// <summary>
    /// TrayQuickOpacityPresets 배열의 i번째 값을 갱신한 새 배열을 반환한다.
    /// 길이가 부족하면 3개로 확장하고 기본값으로 채운다.
    /// </summary>
    private static double[] SetPresetAt(double[] original, int i, double newValue)
    {
        double[] source = original ?? DefaultConfig.TrayQuickOpacityPresets;
        int len = Math.Max(source.Length, 3);
        var copy = new double[len];
        Array.Copy(source, copy, source.Length);
        if (source.Length < len)
        {
            double[] defaults = DefaultConfig.TrayQuickOpacityPresets;
            for (int k = source.Length; k < len && k < defaults.Length; k++)
                copy[k] = defaults[k];
        }
        if (i >= 0 && i < copy.Length) copy[i] = newValue;
        return copy;
    }

}
