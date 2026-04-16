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
    public static AppConfig Apply(AppConfig config)
    {
        if (config.Theme == Theme.Custom)
            return RestoreCustomBackup(config);

        var backed = EnsureBackup(config);
        return config.Theme switch
        {
            Theme.Minimal => backed with
            {
                HangulBg = "#1F2937", HangulFg = "#F9FAFB",
                EnglishBg = "#9CA3AF", EnglishFg = "#111827",
                NonKoreanBg = "#D1D5DB", NonKoreanFg = "#374151",
            },
            Theme.Vivid => backed with
            {
                HangulBg = "#22C55E", HangulFg = "#FFFFFF",
                EnglishBg = "#EF4444", EnglishFg = "#FFFFFF",
                NonKoreanBg = "#3B82F6", NonKoreanFg = "#FFFFFF",
            },
            Theme.Pastel => backed with
            {
                HangulBg = "#86EFAC", HangulFg = "#14532D",
                EnglishBg = "#FDE68A", EnglishFg = "#78350F",
                NonKoreanBg = "#C4B5FD", NonKoreanFg = "#3B0764",
            },
            Theme.Dark => backed with
            {
                HangulBg = "#065F46", HangulFg = "#D1FAE5",
                EnglishBg = "#92400E", EnglishFg = "#FEF3C7",
                NonKoreanBg = "#374151", NonKoreanFg = "#F3F4F6",
            },
            Theme.System => ApplySystemTheme(backed),
            _ => backed,
        };
    }

    /// <summary>
    /// 커스텀 색상이 아직 백업되지 않았으면 현재 색상을 백업 필드에 저장.
    /// </summary>
    private static AppConfig EnsureBackup(AppConfig config)
    {
        if (config.CustomBackupHangulBg is not null)
            return config;
        return config with
        {
            CustomBackupHangulBg = config.HangulBg,
            CustomBackupHangulFg = config.HangulFg,
            CustomBackupEnglishBg = config.EnglishBg,
            CustomBackupEnglishFg = config.EnglishFg,
            CustomBackupNonKoreanBg = config.NonKoreanBg,
            CustomBackupNonKoreanFg = config.NonKoreanFg,
        };
    }

    /// <summary>
    /// 백업이 존재하면 커스텀 색상을 복원하고 백업 필드를 초기화.
    /// </summary>
    private static AppConfig RestoreCustomBackup(AppConfig config)
    {
        if (config.CustomBackupHangulBg is null)
            return config;
        return config with
        {
            HangulBg = config.CustomBackupHangulBg,
            HangulFg = config.CustomBackupHangulFg ?? config.HangulFg,
            EnglishBg = config.CustomBackupEnglishBg ?? config.EnglishBg,
            EnglishFg = config.CustomBackupEnglishFg ?? config.EnglishFg,
            NonKoreanBg = config.CustomBackupNonKoreanBg ?? config.NonKoreanBg,
            NonKoreanFg = config.CustomBackupNonKoreanFg ?? config.NonKoreanFg,
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
        uint accentColor = User32.GetSysColor(Win32Constants.COLOR_HIGHLIGHT);
        var (r, g, b) = ColorHelper.ColorRefToRgb(accentColor);
        string hangulBg = ColorHelper.RgbToHex(r, g, b);
        // 보색 계산
        string englishBg = ColorHelper.RgbToHex((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        return config with { HangulBg = hangulBg, EnglishBg = englishBg };
    }
}
