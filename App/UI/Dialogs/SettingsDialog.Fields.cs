using System.Globalization;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Logging;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// SettingsDialog 의 필드 정의/팩토리/빌더 분할.
/// FieldDef/RowDef 메타데이터, 6개 팩토리(Bool/Int/Dbl/Str/ColorField/Combo),
/// BuildRowDefs(12 섹션), 헬퍼(ReadEdit/GetPresetAt/SetPresetAt/언어 매핑).
/// </summary>
internal static partial class SettingsDialog
{
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
    /// 12개 섹션의 RowDef 리스트를 빌드하며 _fields를 채운다.
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

        // ================================================================
        // 1. 표시 모드
        // ================================================================
        Sec("표시 모드", "Display Mode");
        Add(Combo("표시 방식", "Display mode",
            ko ? ["이벤트 시", "항상"] : ["On Event", "Always"],
            c => (int)c.DisplayMode,
            (c, i) => c with { DisplayMode = (DisplayMode)Math.Clamp(i, 0, 1) }));
        Add(Int("이벤트 표시 시간 (ms)", "Event display duration (ms)", 500, 10000,
            c => c.EventDisplayDurationMs,
            (c, v) => c with { EventDisplayDurationMs = v }));
        Add(Int("항상 모드 유휴 전환 (ms)", "Always-mode idle timeout (ms)", 1000, 30000,
            c => c.AlwaysIdleTimeoutMs,
            (c, v) => c with { AlwaysIdleTimeoutMs = v }));
        Add(Bool("포커스 변경 시 이벤트", "Trigger on focus change",
            c => c.EventTriggers.OnFocusChange,
            (c, v) => c with { EventTriggers = c.EventTriggers with { OnFocusChange = v } }));
        Add(Bool("IME 변경 시 이벤트", "Trigger on IME change",
            c => c.EventTriggers.OnImeChange,
            (c, v) => c with { EventTriggers = c.EventTriggers with { OnImeChange = v } }));

        // ================================================================
        // 2. 외관 — 크기·테두리
        // ================================================================
        Sec("외관 — 크기·테두리", "Appearance — Size & Border");
        Add(Int("라벨 너비 (px)", "Label width (px)", 16, 128,
            c => c.LabelWidth, (c, v) => c with { LabelWidth = v }));
        Add(Int("라벨 높이 (px)", "Label height (px)", 12, 96,
            c => c.LabelHeight, (c, v) => c with { LabelHeight = v }));
        Add(Int("테두리 둥글기 (px)", "Border radius (px)", 0, 48,
            c => c.LabelBorderRadius, (c, v) => c with { LabelBorderRadius = v }));
        Add(Int("테두리 두께 (px)", "Border width (px)", 0, 8,
            c => c.BorderWidth, (c, v) => c with { BorderWidth = v }));
        Add(ColorField("테두리 색상", "Border color",
            c => c.BorderColor, (c, v) => c with { BorderColor = v }));

        // ================================================================
        // 3. 외관 — 색상·투명도
        // ================================================================
        Sec("외관 — 색상·투명도", "Appearance — Colors & Opacity");
        Add(ColorField("한글 배경색", "Hangul background",
            c => c.HangulBg, (c, v) => c with { HangulBg = v }));
        Add(ColorField("한글 글자색", "Hangul foreground",
            c => c.HangulFg, (c, v) => c with { HangulFg = v }));
        Add(ColorField("영문 배경색", "English background",
            c => c.EnglishBg, (c, v) => c with { EnglishBg = v }));
        Add(ColorField("영문 글자색", "English foreground",
            c => c.EnglishFg, (c, v) => c with { EnglishFg = v }));
        Add(ColorField("비한국어 배경색", "Non-Korean background",
            c => c.NonKoreanBg, (c, v) => c with { NonKoreanBg = v }));
        Add(ColorField("비한국어 글자색", "Non-Korean foreground",
            c => c.NonKoreanFg, (c, v) => c with { NonKoreanFg = v }));
        Add(Dbl("유휴 투명도", "Idle opacity", 0.1, 1.0,
            c => c.IdleOpacity, (c, v) => c with { IdleOpacity = v }));
        Add(Dbl("활성 투명도", "Active opacity", 0.1, 1.0,
            c => c.ActiveOpacity, (c, v) => c with { ActiveOpacity = v }));

        // ================================================================
        // 4. 외관 — 텍스트
        // ================================================================
        Sec("외관 — 텍스트", "Appearance — Text");
        Add(Str("글꼴", "Font family",
            c => c.FontFamily, (c, v) => c with { FontFamily = v }, allowEmpty: false));
        Add(Int("글꼴 크기", "Font size", 8, 36,
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
        // 5. 외관 — 테마
        // ================================================================
        Sec("외관 — 테마", "Appearance — Theme");
        Add(Combo("테마", "Theme",
            ko ? ["사용자 지정", "미니멀", "비비드", "파스텔", "다크", "시스템"]
               : ["Custom", "Minimal", "Vivid", "Pastel", "Dark", "System"],
            c => (int)c.Theme,
            (c, i) => c with { Theme = (Theme)Math.Clamp(i, 0, 5) }));

        // ================================================================
        // 6. 애니메이션
        // ================================================================
        // AnimationEnabled / ChangeHighlight는 트레이 메뉴에서 토글 가능하므로 여기서 제외.
        Sec("애니메이션", "Animation");
        Add(Int("페이드 인 (ms)", "Fade in (ms)", 0, 2000,
            c => c.FadeInMs, (c, v) => c with { FadeInMs = v }));
        Add(Int("페이드 아웃 (ms)", "Fade out (ms)", 0, 2000,
            c => c.FadeOutMs, (c, v) => c with { FadeOutMs = v }));
        Add(Dbl("강조 배율", "Highlight scale", 1.0, 2.0,
            c => c.HighlightScale, (c, v) => c with { HighlightScale = v }));
        Add(Int("강조 지속 시간 (ms)", "Highlight duration (ms)", 0, 2000,
            c => c.HighlightDurationMs, (c, v) => c with { HighlightDurationMs = v }));
        Add(Bool("슬라이드 애니메이션", "Slide animation",
            c => c.SlideAnimation, (c, v) => c with { SlideAnimation = v }));
        Add(Int("슬라이드 속도 (ms)", "Slide speed (ms)", 0, 2000,
            c => c.SlideSpeedMs, (c, v) => c with { SlideSpeedMs = v }));

        // ================================================================
        // 7. 감지 및 숨김
        // ================================================================
        Sec("감지 및 숨김", "Detection & Hiding");
        Add(Int("감지 주기 (ms)", "Poll interval (ms)", 50, 500,
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
        // 8. 앱별 프로필
        // ================================================================
        Sec("앱별 프로필", "App Profiles");
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
        // 9. 트레이
        // ================================================================
        Sec("트레이", "Tray");
        Add(Bool("툴팁 표시", "Show tooltip",
            c => c.TrayTooltip, (c, v) => c with { TrayTooltip = v }));
        Add(Combo("좌클릭 동작", "Left-click action",
            ko ? ["표시 토글", "설정 파일 열기", "동작 없음"]
               : ["Toggle visibility", "Open settings", "None"],
            c => (int)c.TrayClickAction,
            (c, i) => c with { TrayClickAction = (TrayClickAction)Math.Clamp(i, 0, 2) }));
        Add(Dbl("빠른 투명도 1 (진하게)", "Quick opacity 1 (High)", 0.1, 1.0,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 0, 0.95),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 0, v) }));
        Add(Dbl("빠른 투명도 2 (보통)", "Quick opacity 2 (Normal)", 0.1, 1.0,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 1, 0.85),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 1, v) }));
        Add(Dbl("빠른 투명도 3 (연하게)", "Quick opacity 3 (Low)", 0.1, 1.0,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 2, 0.6),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 2, v) }));

        // ================================================================
        // 10. 시스템
        // ================================================================
        Sec("시스템", "System");
        Add(Combo("언어", "Language",
            ko ? ["자동", "한국어", "English"] : ["Auto", "Korean", "English"],
            c => LanguageToIndex(c.Language),
            (c, i) => c with { Language = IndexToLanguage(i) }));
        Add(Combo("로그 레벨", "Log level",
            ko ? ["디버그", "정보", "경고", "오류"] : ["Debug", "Info", "Warning", "Error"],
            c => (int)c.LogLevel,
            (c, i) => c with { LogLevel = (LogLevel)Math.Clamp(i, 0, 3) }));
        Add(Bool("파일에 로그 기록", "Log to file",
            c => c.LogToFile, (c, v) => c with { LogToFile = v }));
        Add(Str("로그 파일 경로", "Log file path",
            c => c.LogFilePath, (c, v) => c with { LogFilePath = v }, allowEmpty: true));
        Add(Int("로그 최대 크기 (MB)", "Log max size (MB)", 1, 100,
            c => c.LogMaxSizeMb, (c, v) => c with { LogMaxSizeMb = v }));
        Add(Bool("부팅 시 업데이트 확인", "Check for updates on startup",
            c => c.UpdateCheckEnabled, (c, v) => c with { UpdateCheckEnabled = v }));

        // ================================================================
        // 11. 인디케이터 조작
        // ================================================================
        Sec("인디케이터 조작", "Indicator Interaction");
        Add(Int("창 스냅 간격 (px)", "Snap gap (px)", 0, 10,
            c => c.SnapGapPx, (c, v) => c with { SnapGapPx = v }));
        Add(Combo("드래그 활성 키", "Drag modifier",
            ko ? ["없음", "Ctrl", "Alt", "Ctrl + Alt"]
               : ["None", "Ctrl", "Alt", "Ctrl + Alt"],
            c => (int)c.DragModifier,
            (c, i) => c with { DragModifier = (DragModifier)Math.Clamp(i, 0, 3) }));

        // ================================================================
        // 12. 고급
        // ================================================================
        Sec("고급", "Advanced");
        Add(Int("TOPMOST 강제 주기 (ms)", "Force topmost interval (ms)", 0, 60000,
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
            GetString = cfg => get(cfg).ToString("0.###", CultureInfo.InvariantCulture),
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidNumberFmt, label));
                if (v < min || v > max)
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsOutOfRangeFmt, label,
                        min.ToString("0.##", CultureInfo.InvariantCulture),
                        max.ToString("0.##", CultureInfo.InvariantCulture)));
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
        char[] buf = new char[len + 2];
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
        double[] source = original ?? [0.95, 0.85, 0.6];
        int len = Math.Max(source.Length, 3);
        var copy = new double[len];
        Array.Copy(source, copy, source.Length);
        if (source.Length < len)
        {
            double[] defaults = [0.95, 0.85, 0.6];
            for (int k = source.Length; k < len && k < defaults.Length; k++)
                copy[k] = defaults[k];
        }
        if (i >= 0 && i < copy.Length) copy[i] = newValue;
        return copy;
    }

    /// <summary>"ko"/"en"/"auto" 문자열을 콤보박스 인덱스(0=auto, 1=ko, 2=en)로 매핑.</summary>
    private static int LanguageToIndex(string lang) => lang switch
    {
        "ko" => 1,
        "en" => 2,
        _    => 0,
    };

    /// <summary>콤보박스 인덱스(0=auto, 1=ko, 2=en)를 설정 문자열로 매핑.</summary>
    private static string IndexToLanguage(int i) => i switch
    {
        1 => "ko",
        2 => "en",
        _ => "auto",
    };
}
