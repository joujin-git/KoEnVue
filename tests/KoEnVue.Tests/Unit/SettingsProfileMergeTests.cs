using System.Collections.Generic;
using System.Text.Json;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="Settings.MatchProfile"/> / <see cref="Settings.MergeProfile"/> 회귀 가드.
/// HWND 없는 순수 JSON/키 경로만 박제 (ResolveMatchKey 는 Win32 의존으로 제외).
/// </summary>
public class SettingsProfileMergeTests
{
    [Fact]
    public void MergeProfile_EmptyObject_ReturnsSameReference()
    {
        var global = new AppConfig() with { Opacity = 0.8 };
        using var doc = JsonDocument.Parse("{}");
        AppConfig merged = Settings.MergeProfile(global, doc.RootElement);
        Assert.Same(global, merged);
    }

    [Fact]
    public void MergeProfile_EnabledTrueOnly_ReturnsSameReference()
    {
        var global = new AppConfig() with { Opacity = 0.8 };
        using var doc = JsonDocument.Parse("""{"enabled":true}""");
        AppConfig merged = Settings.MergeProfile(global, doc.RootElement);
        Assert.Same(global, merged);
    }

    [Fact]
    public void MergeProfile_OpacityOverride_PreservesSiblings()
    {
        var global = new AppConfig() with
        {
            Opacity = 0.9,
            HangulBg = "#111111",
            EnglishBg = "#222222",
        };
        using var doc = JsonDocument.Parse("""{"opacity":0.5}""");
        AppConfig merged = Settings.MergeProfile(global, doc.RootElement);
        Assert.Equal(0.5, merged.Opacity);
        Assert.Equal("#111111", merged.HangulBg);
        Assert.Equal("#222222", merged.EnglishBg);
        Assert.NotSame(global, merged);
    }

    [Fact]
    public void MatchProfile_ProcessHit_MergesOverride()
    {
        var profiles = new Dictionary<string, JsonElement>
        {
            ["notepad"] = JsonDocument.Parse("""{"opacity":0.4}""").RootElement.Clone(),
        };
        var global = new AppConfig() with
        {
            AppProfileMatch = AppProfileMatch.Process,
            AppProfiles = profiles,
            Opacity = 0.9,
        };
        AppConfig? resolved = Settings.MatchProfile(global, "notepad");
        Assert.NotNull(resolved);
        Assert.Equal(0.4, resolved!.Opacity);
    }

    [Fact]
    public void MatchProfile_ProcessMiss_ReturnsGlobal()
    {
        var global = new AppConfig() with
        {
            AppProfileMatch = AppProfileMatch.Process,
            AppProfiles = new Dictionary<string, JsonElement>
            {
                ["notepad"] = JsonDocument.Parse("""{"opacity":0.4}""").RootElement.Clone(),
            },
        };
        AppConfig? resolved = Settings.MatchProfile(global, "chrome");
        Assert.Same(global, resolved);
    }

    [Fact]
    public void MatchProfile_ProcessDisabled_ReturnsNull()
    {
        var global = new AppConfig() with
        {
            AppProfileMatch = AppProfileMatch.Process,
            AppProfiles = new Dictionary<string, JsonElement>
            {
                ["notepad"] = JsonDocument.Parse("""{"enabled":false}""").RootElement.Clone(),
            },
        };
        Assert.Null(Settings.MatchProfile(global, "notepad"));
    }

    [Fact]
    public void MatchProfile_TitleRegexHit_Merges()
    {
        var global = new AppConfig() with
        {
            AppProfileMatch = AppProfileMatch.Title,
            AppProfiles = new Dictionary<string, JsonElement>
            {
                [@"^Foo.*"] = JsonDocument.Parse("""{"opacity":0.3}""").RootElement.Clone(),
            },
            Opacity = 0.9,
        };
        AppConfig? resolved = Settings.MatchProfile(global, "Foo Bar");
        Assert.NotNull(resolved);
        Assert.Equal(0.3, resolved!.Opacity);
    }

    [Fact]
    public void MatchProfile_TitleInvalidPattern_SkipsAndFallsBack()
    {
        var global = new AppConfig() with
        {
            AppProfileMatch = AppProfileMatch.Title,
            AppProfiles = new Dictionary<string, JsonElement>
            {
                ["("] = JsonDocument.Parse("""{"opacity":0.1}""").RootElement.Clone(),
            },
            Opacity = 0.9,
        };
        AppConfig? resolved = Settings.MatchProfile(global, "anything");
        Assert.Same(global, resolved);
    }
}
