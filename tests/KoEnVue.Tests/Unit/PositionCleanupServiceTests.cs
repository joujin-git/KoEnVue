using KoEnVue.App.Config;
using KoEnVue.App.Localization;
using KoEnVue.App.Models;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="PositionCleanupService.FormatDisplayLabel"/> / <see cref="PositionCleanupService.RemoveSelected"/>
/// 모드 태그·양쪽 dict 동시 삭제 회귀 가드.
/// </summary>
public class PositionCleanupServiceTests
{
    public PositionCleanupServiceTests()
    {
        // FormatDisplayLabel 이 I18n 테이블을 읽으므로 테스트마다 ko 고정.
        I18n.Load(AppLanguage.Ko);
    }

    [Theory]
    [InlineData(true, false, false, "notepad (고정)")]
    [InlineData(false, true, false, "notepad (창)")]
    [InlineData(true, true, false, "notepad (고정·창)")]
    [InlineData(true, false, true, "notepad (고정, 실행 중)")]
    [InlineData(false, true, true, "notepad (창, 실행 중)")]
    [InlineData(true, true, true, "notepad (고정·창, 실행 중)")]
    public void FormatDisplayLabel_KoreanTags(
        bool hasFixed, bool hasRelative, bool isRunning, string expected)
    {
        Assert.Equal(expected,
            PositionCleanupService.FormatDisplayLabel("notepad", hasFixed, hasRelative, isRunning));
    }

    [Fact]
    public void FormatDisplayLabel_EnglishTags()
    {
        I18n.Load(AppLanguage.En);
        Assert.Equal("chrome (Fixed·Window, running)",
            PositionCleanupService.FormatDisplayLabel("chrome", true, true, true));
    }

    [Fact]
    public void RemoveSelected_RemovesFromBothDicts()
    {
        var cfg = new AppConfig() with
        {
            IndicatorPositions = new Dictionary<string, int[]>
            {
                ["keep"] = [1, 2],
                ["gone"] = [3, 4],
            },
            IndicatorPositionsRelative = new Dictionary<string, int[]>
            {
                ["keep"] = [0, 1, 2],
                ["gone"] = [1, 5, 6],
                ["windowOnly"] = [2, 0, 0],
            },
        };

        var display = new List<string> { "gone (고정·창)", "windowOnly (창)" };
        var original = new List<string> { "gone", "windowOnly" };
        var selected = new List<string> { "gone (고정·창)" };

        AppConfig cleaned = PositionCleanupService.RemoveSelected(cfg, display, original, selected);

        Assert.False(cleaned.IndicatorPositions.ContainsKey("gone"));
        Assert.False(cleaned.IndicatorPositionsRelative.ContainsKey("gone"));
        Assert.True(cleaned.IndicatorPositions.ContainsKey("keep"));
        Assert.True(cleaned.IndicatorPositionsRelative.ContainsKey("keep"));
        Assert.True(cleaned.IndicatorPositionsRelative.ContainsKey("windowOnly"));
    }

    [Fact]
    public void Compute_UnionsKeys_AndTagsModes()
    {
        var cfg = new AppConfig() with
        {
            IndicatorPositions = new Dictionary<string, int[]> { ["a"] = [0, 0] },
            IndicatorPositionsRelative = new Dictionary<string, int[]>
            {
                ["a"] = [0, 0, 0],
                ["b"] = [1, 0, 0],
            },
        };

        var (display, original) = PositionCleanupService.Compute(cfg);
        Assert.Equal(2, display.Count);
        Assert.Equal(2, original.Count);
        Assert.Contains("a", original);
        Assert.Contains("b", original);

        int ia = original.IndexOf("a");
        int ib = original.IndexOf("b");
        Assert.Contains("(고정·창", display[ia]);
        Assert.Contains("(창", display[ib]);
        Assert.DoesNotContain("(고정·창", display[ib]);
    }
}
