using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.Config;

/// <summary>
/// 테마 프리셋 적용. theme 값에 따라 색상 오버라이드.
/// Custom이면 백업에서 원래 커스텀 색상 복원. 프리셋이면 커스텀 색상을 백업 후 프리셋 적용.
/// </summary>
internal static class ThemePresets
{
    /// <summary>
    /// 6-쌍 테마 색상 묶음. record 로 묶어 4 preset 의 필드별 반복을 제거한다 (D2).
    /// internal 이고 STJ context 에 등록하지 않으므로 직렬화되지 않는다.
    /// </summary>
    internal sealed record ThemeColors(
        string HBg, string HFg,
        string EBg, string EFg,
        string NBg, string NFg);

    /// <summary>
    /// 정적 프리셋 — Minimal/Vivid/Pastel/Dark. System 은 ApplySystemTheme 가 런타임 계산하므로 제외.
    /// </summary>
    private static readonly Dictionary<Theme, ThemeColors> _presets = new()
    {
        [Theme.Minimal] = new("#1F2937", "#F9FAFB", "#9CA3AF", "#111827", "#D1D5DB", "#374151"),
        [Theme.Vivid]   = new("#22C55E", "#FFFFFF", "#EF4444", "#FFFFFF", "#3B82F6", "#FFFFFF"),
        [Theme.Pastel]  = new("#86EFAC", "#14532D", "#FDE68A", "#78350F", "#C4B5FD", "#3B0764"),
        [Theme.Dark]    = new("#065F46", "#D1FAE5", "#92400E", "#FEF3C7", "#374151", "#F3F4F6"),
    };

    public static AppConfig Apply(AppConfig config)
    {
        if (config.Theme == Theme.Custom)
            return RestoreCustomBackup(config);

        var backed = EnsureBackup(config);
        if (_presets.TryGetValue(config.Theme, out var colors))
        {
            return backed with
            {
                HangulBg = colors.HBg, HangulFg = colors.HFg,
                EnglishBg = colors.EBg, EnglishFg = colors.EFg,
                NonKoreanBg = colors.NBg, NonKoreanFg = colors.NFg,
            };
        }
        return config.Theme == Theme.System ? ApplySystemTheme(backed) : backed;
    }

    /// <summary>
    /// 커스텀 색상 6쌍을 백업한다. 필드별 개별 null 체크로 과거의 부분 백업 손상 케이스도 복구.
    /// 이미 6개 전부 채워진 경우 record with 할당을 생략한다 (대다수 경로의 fast path).
    /// </summary>
    private static AppConfig EnsureBackup(AppConfig config)
    {
        if (config.CustomBackupHangulBg is not null
            && config.CustomBackupHangulFg is not null
            && config.CustomBackupEnglishBg is not null
            && config.CustomBackupEnglishFg is not null
            && config.CustomBackupNonKoreanBg is not null
            && config.CustomBackupNonKoreanFg is not null)
            return config;

        return config with
        {
            CustomBackupHangulBg = config.CustomBackupHangulBg ?? config.HangulBg,
            CustomBackupHangulFg = config.CustomBackupHangulFg ?? config.HangulFg,
            CustomBackupEnglishBg = config.CustomBackupEnglishBg ?? config.EnglishBg,
            CustomBackupEnglishFg = config.CustomBackupEnglishFg ?? config.EnglishFg,
            CustomBackupNonKoreanBg = config.CustomBackupNonKoreanBg ?? config.NonKoreanBg,
            CustomBackupNonKoreanFg = config.CustomBackupNonKoreanFg ?? config.NonKoreanFg,
        };
    }

    /// <summary>
    /// 백업 6개가 모두 존재할 때만 원자적으로 복원한다. 부분 손상(일부 null)이면 프리셋 색상을
    /// 유지하고 복원을 건너뛴다 — 남은 백업 필드들로 커스텀 색이 프리셋 색으로 소리 없이
    /// 덮여 영구 고정되는 데이터 손실을 차단.
    /// </summary>
    private static AppConfig RestoreCustomBackup(AppConfig config)
    {
        if (config.CustomBackupHangulBg is null
            || config.CustomBackupHangulFg is null
            || config.CustomBackupEnglishBg is null
            || config.CustomBackupEnglishFg is null
            || config.CustomBackupNonKoreanBg is null
            || config.CustomBackupNonKoreanFg is null)
            return config;

        return config with
        {
            HangulBg = config.CustomBackupHangulBg,
            HangulFg = config.CustomBackupHangulFg,
            EnglishBg = config.CustomBackupEnglishBg,
            EnglishFg = config.CustomBackupEnglishFg,
            NonKoreanBg = config.CustomBackupNonKoreanBg,
            NonKoreanFg = config.CustomBackupNonKoreanFg,
            CustomBackupHangulBg = null,
            CustomBackupHangulFg = null,
            CustomBackupEnglishBg = null,
            CustomBackupEnglishFg = null,
            CustomBackupNonKoreanBg = null,
            CustomBackupNonKoreanFg = null,
        };
    }

    private static AppConfig ApplySystemTheme(AppConfig config)
    {
        // 데이터 소스 우선순위:
        //   1. 고대비 모드 (PR-05 H4-c) — SPI_GETHIGHCONTRAST 가 ON 이면 OS 가 보장하는 contrast-safe
        //      팔레트 (HIGHLIGHT/HIGHLIGHTTEXT/WINDOW/WINDOWTEXT) 를 사용. 보색 계산 결과가 명도 대비를
        //      깨뜨릴 수 있어 가독성을 우선 보호한다.
        //   2. DwmGetColorizationColor (PR-14) — Win11 personalization accent 의 source-of-truth.
        //      "제목 표시줄과 창 테두리에 강조색 표시" 옵션 OFF 에서도 정확히 추적.
        //   3. GetSysColor(COLOR_HIGHLIGHT) — DWM composition 비활성 등 마지막 폴백.
        if (User32.IsHighContrastEnabled())
        {
            string hBg = SysColorHex(Win32Constants.COLOR_HIGHLIGHT);
            string hFg = SysColorHex(Win32Constants.COLOR_HIGHLIGHTTEXT);
            string eBg = SysColorHex(Win32Constants.COLOR_WINDOW);
            string eFg = SysColorHex(Win32Constants.COLOR_WINDOWTEXT);
            return config with
            {
                HangulBg = hBg, HangulFg = hFg,
                EnglishBg = eBg, EnglishFg = eFg,
            };
        }

        if (!Dwmapi.TryGetColorizationRgb(out byte r, out byte g, out byte b))
        {
            uint accentColor = User32.GetSysColor(Win32Constants.COLOR_HIGHLIGHT);
            (r, g, b) = ColorHelper.ColorRefToRgb(accentColor);
        }
        string hangulBg = ColorHelper.RgbToHex(r, g, b);
        // 보색 계산
        string englishBg = ColorHelper.RgbToHex((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        return config with { HangulBg = hangulBg, EnglishBg = englishBg };
    }

    private static string SysColorHex(int sysColorIndex)
    {
        uint c = User32.GetSysColor(sysColorIndex);
        var (r, g, b) = ColorHelper.ColorRefToRgb(c);
        return ColorHelper.RgbToHex(r, g, b);
    }
}
