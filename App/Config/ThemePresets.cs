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
        uint accentColor = User32.GetSysColor(Win32Constants.COLOR_HIGHLIGHT);
        var (r, g, b) = ColorHelper.ColorRefToRgb(accentColor);
        string hangulBg = ColorHelper.RgbToHex(r, g, b);
        // 보색 계산
        string englishBg = ColorHelper.RgbToHex((byte)(255 - r), (byte)(255 - g), (byte)(255 - b));
        return config with { HangulBg = hangulBg, EnglishBg = englishBg };
    }
}
