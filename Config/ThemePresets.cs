using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.Config;

/// <summary>
/// 테마 프리셋 적용. theme 값에 따라 색상 오버라이드.
/// Custom이면 사용자 색상 그대로 통과.
/// </summary>
internal static class ThemePresets
{
    public static AppConfig Apply(AppConfig config)
    {
        return config.Theme switch
        {
            Theme.Custom => config,
            Theme.Minimal => config with
            {
                HangulBg = "#1F2937", HangulFg = "#F9FAFB",
                EnglishBg = "#9CA3AF", EnglishFg = "#111827",
                NonKoreanBg = "#D1D5DB", NonKoreanFg = "#374151",
            },
            Theme.Vivid => config with
            {
                HangulBg = "#22C55E", HangulFg = "#FFFFFF",
                EnglishBg = "#EF4444", EnglishFg = "#FFFFFF",
                NonKoreanBg = "#3B82F6", NonKoreanFg = "#FFFFFF",
            },
            Theme.Pastel => config with
            {
                HangulBg = "#86EFAC", HangulFg = "#14532D",
                EnglishBg = "#FDE68A", EnglishFg = "#78350F",
                NonKoreanBg = "#C4B5FD", NonKoreanFg = "#3B0764",
            },
            Theme.Dark => config with
            {
                HangulBg = "#065F46", HangulFg = "#D1FAE5",
                EnglishBg = "#92400E", EnglishFg = "#FEF3C7",
                NonKoreanBg = "#374151", NonKoreanFg = "#F3F4F6",
            },
            Theme.System => ApplySystemTheme(config),
            _ => config,
        };
    }

    private static AppConfig ApplySystemTheme(AppConfig config)
    {
        uint accentColor = User32.GetSysColor(Win32Constants.COLOR_HIGHLIGHT);
        var (r, g, b) = ColorHelper.ColorRefToRgb(accentColor);
        string hangulBg = ColorHelper.RgbToHex(r, g, b);
        // 보색 계산
        string englishBg = ColorHelper.RgbToHex((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        return config with { HangulBg = hangulBg, EnglishBg = englishBg };
    }
}
