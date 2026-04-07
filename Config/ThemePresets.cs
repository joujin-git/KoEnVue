using KoEnVue.Models;
using KoEnVue.Native;

namespace KoEnVue.Config;

/// <summary>
/// 테마 프리셋 적용. theme 값에 따라 색상 오버라이드.
/// "custom"이면 사용자 색상 그대로 통과.
/// </summary>
internal static class ThemePresets
{
    // P3: 매직 넘버 금지
    private const int COLOR_HIGHLIGHT = 13;

    public static AppConfig Apply(AppConfig config)
    {
        return config.Theme switch
        {
            "custom" => config,
            "minimal" => config with
            {
                HangulBg = "#1F2937", HangulFg = "#F9FAFB",
                EnglishBg = "#9CA3AF", EnglishFg = "#111827",
                NonKoreanBg = "#D1D5DB", NonKoreanFg = "#374151",
            },
            "vivid" => config with
            {
                HangulBg = "#22C55E", HangulFg = "#FFFFFF",
                EnglishBg = "#EF4444", EnglishFg = "#FFFFFF",
                NonKoreanBg = "#3B82F6", NonKoreanFg = "#FFFFFF",
            },
            "pastel" => config with
            {
                HangulBg = "#86EFAC", HangulFg = "#14532D",
                EnglishBg = "#FDE68A", EnglishFg = "#78350F",
                NonKoreanBg = "#C4B5FD", NonKoreanFg = "#3B0764",
            },
            "dark" => config with
            {
                HangulBg = "#065F46", HangulFg = "#D1FAE5",
                EnglishBg = "#92400E", EnglishFg = "#FEF3C7",
                NonKoreanBg = "#374151", NonKoreanFg = "#F3F4F6",
            },
            "system" => ApplySystemTheme(config),
            _ => config,
        };
    }

    private static AppConfig ApplySystemTheme(AppConfig config)
    {
        uint accentColor = User32.GetSysColor(COLOR_HIGHLIGHT);
        // COLORREF(0x00BBGGRR) → RGB 분리
        byte r = (byte)(accentColor & 0xFF);
        byte g = (byte)((accentColor >> 8) & 0xFF);
        byte b = (byte)((accentColor >> 16) & 0xFF);
        string hangulBg = $"#{r:X2}{g:X2}{b:X2}";
        // 보색 계산
        string englishBg = $"#{255 - r:X2}{255 - g:X2}{255 - b:X2}";
        return config with { HangulBg = hangulBg, EnglishBg = englishBg };
    }
}
