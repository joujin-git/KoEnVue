using KoEnVue.App.Config;
using KoEnVue.App.Models;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="ThemePresets.Apply"/> 의 커스텀 백업/복원·프리셋 색 적용 회귀 가드.
/// System 테마(DWM/고대비) 경로는 Win32 의존이라 제외 — Custom/Minimal 순수 경로만 박제.
/// </summary>
public class ThemePresetsBackupTests
{
    private static AppConfig CustomWithFullBackup() => new AppConfig() with
    {
        Theme = Theme.Custom,
        HangulBg = "#AAAAAA",
        HangulFg = "#BBBBBB",
        EnglishBg = "#CCCCCC",
        EnglishFg = "#DDDDDD",
        NonKoreanBg = "#EEEEEE",
        NonKoreanFg = "#FFFFFF",
        CustomBackupHangulBg = "#111111",
        CustomBackupHangulFg = "#222222",
        CustomBackupEnglishBg = "#333333",
        CustomBackupEnglishFg = "#444444",
        CustomBackupNonKoreanBg = "#555555",
        CustomBackupNonKoreanFg = "#666666",
    };

    [Fact]
    public void Apply_Custom_FullBackup_RestoresAtomicallyAndClearsBackup()
    {
        AppConfig restored = ThemePresets.Apply(CustomWithFullBackup());
        Assert.Equal(Theme.Custom, restored.Theme);
        Assert.Equal("#111111", restored.HangulBg);
        Assert.Equal("#222222", restored.HangulFg);
        Assert.Equal("#333333", restored.EnglishBg);
        Assert.Equal("#444444", restored.EnglishFg);
        Assert.Equal("#555555", restored.NonKoreanBg);
        Assert.Equal("#666666", restored.NonKoreanFg);
        Assert.Null(restored.CustomBackupHangulBg);
        Assert.Null(restored.CustomBackupHangulFg);
        Assert.Null(restored.CustomBackupEnglishBg);
        Assert.Null(restored.CustomBackupEnglishFg);
        Assert.Null(restored.CustomBackupNonKoreanBg);
        Assert.Null(restored.CustomBackupNonKoreanFg);
    }

    [Fact]
    public void Apply_Custom_PartialBackup_RejectsRestore_KeepsCurrentColors()
    {
        var cfg = CustomWithFullBackup() with
        {
            CustomBackupEnglishFg = null, // 부분 손상
        };
        AppConfig result = ThemePresets.Apply(cfg);
        Assert.Equal("#AAAAAA", result.HangulBg);
        Assert.Equal("#CCCCCC", result.EnglishBg);
        Assert.Null(result.CustomBackupEnglishFg);
        Assert.Equal("#111111", result.CustomBackupHangulBg); // 기존 백업 필드 유지
    }

    [Fact]
    public void Apply_Minimal_PartialBackup_FillsNullSlotsThenAppliesPreset()
    {
        var cfg = new AppConfig() with
        {
            Theme = Theme.Minimal,
            HangulBg = "#AAAAAA",
            HangulFg = "#BBBBBB",
            EnglishBg = "#CCCCCC",
            EnglishFg = "#DDDDDD",
            NonKoreanBg = "#EEEEEE",
            NonKoreanFg = "#FFFFFF",
            CustomBackupHangulBg = "#111111", // 이미 채워진 슬롯은 보존
            CustomBackupHangulFg = null,
        };
        AppConfig applied = ThemePresets.Apply(cfg);
        Assert.Equal(Theme.Minimal, applied.Theme);
        Assert.Equal("#111111", applied.CustomBackupHangulBg);
        Assert.Equal("#BBBBBB", applied.CustomBackupHangulFg); // null 슬롯만 현재색으로 채움
        Assert.Equal("#1F2937", applied.HangulBg); // Minimal 프리셋
        Assert.Equal("#F9FAFB", applied.HangulFg);
        Assert.Equal("#9CA3AF", applied.EnglishBg);
        Assert.Equal("#111827", applied.EnglishFg);
        Assert.Equal("#D1D5DB", applied.NonKoreanBg);
        Assert.Equal("#374151", applied.NonKoreanFg);
    }
}
