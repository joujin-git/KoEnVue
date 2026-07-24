using System.Text.Json;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>PR-29/30/31 커서 표시 — Soft/Sharp/Motion·마이그·안개 알파.</summary>
public class CursorMotionDimTests
{
    [Fact]
    public void AdvanceForMode_Soft_AlwaysDim()
    {
        int still = 0;
        Assert.True(CursorMotionDim.AdvanceForMode(
            CursorDisplayMode.Soft, ref still, moving: false, wasDimActive: false, settlePolls: 8));
        Assert.True(CursorMotionDim.AdvanceForMode(
            CursorDisplayMode.Soft, ref still, moving: true, wasDimActive: true, settlePolls: 8));
        Assert.Equal(0, still);
    }

    [Fact]
    public void AdvanceForMode_Sharp_NeverDim()
    {
        int still = 3;
        Assert.False(CursorMotionDim.AdvanceForMode(
            CursorDisplayMode.Sharp, ref still, moving: true, wasDimActive: true, settlePolls: 8));
        Assert.Equal(0, still);
    }

    [Fact]
    public void AdvanceDimActive_Moving_EntersImmediately()
    {
        int still = 5;
        bool dim = CursorMotionDim.AdvanceDimActive(ref still, moving: true, wasDimActive: false,
            settlePolls: DefaultConfig.CursorMotionDimSettlePolls);
        Assert.True(dim);
        Assert.Equal(0, still);
    }

    [Fact]
    public void AdvanceDimActive_ExitRequiresSettlePolls()
    {
        const int settle = DefaultConfig.CursorMotionDimSettlePolls;
        int still = 0;
        bool dim = true;
        for (int i = 1; i < settle; i++)
        {
            dim = CursorMotionDim.AdvanceDimActive(ref still, moving: false, wasDimActive: dim, settlePolls: settle);
            Assert.True(dim);
            Assert.Equal(i, still);
        }

        dim = CursorMotionDim.AdvanceDimActive(ref still, moving: false, wasDimActive: dim, settlePolls: settle);
        Assert.False(dim);
        Assert.Equal(0, still);
    }

    [Fact]
    public void AdvanceDimActive_MovingResetsSettleCounter()
    {
        const int settle = DefaultConfig.CursorMotionDimSettlePolls;
        int still = 0;
        bool dim = true;
        dim = CursorMotionDim.AdvanceDimActive(ref still, moving: false, wasDimActive: dim, settlePolls: settle);
        Assert.True(dim);
        Assert.Equal(1, still);

        dim = CursorMotionDim.AdvanceDimActive(ref still, moving: true, wasDimActive: dim, settlePolls: settle);
        Assert.True(dim);
        Assert.Equal(0, still);
    }

    [Fact]
    public void DimThreshold_IsLowerThanHideThreshold()
    {
        Assert.True(DefaultConfig.CursorMotionDimThresholdPx < DefaultConfig.CursorMotionThresholdPx);
        Assert.Equal(1, DefaultConfig.CursorMotionDimThresholdPx);
    }

    [Fact]
    public void EffectiveSoftness_DimActive_KeepsSoftnessRegardlessOfFormerPopBehavior()
    {
        Assert.Equal(0.35, CursorMotionDim.EffectiveSoftness(dimActive: true, softness: 0.35));
        Assert.Equal(0.0, CursorMotionDim.EffectiveSoftness(dimActive: false, softness: 0.35));
    }

    [Fact]
    public void RingAlphas_FogNearlyUniformAcrossThreeRings()
    {
        var dim = CursorMotionDim.RingAlphas(true, DefaultConfig.CursorMotionAlpha,
            DefaultConfig.CursorMotionRingInnerFactor,
            DefaultConfig.CursorMotionRingMiddleFactor,
            DefaultConfig.CursorMotionRingOuterFactor,
            DefaultConfig.MinCursorMotionAlpha);
        Assert.Equal(DefaultConfig.CursorMotionAlpha, dim.Inner);
        Assert.True(dim.Outer >= dim.Inner * 0.80);
        Assert.True(dim.Middle >= dim.Inner * 0.90);
    }

    [Theory]
    [InlineData("""{"cursor_motion_dim_enabled":true}""", "motion")]
    [InlineData("""{"cursor_motion_dim_enabled":false}""", "sharp")]
    [InlineData("""{}""", "soft")]
    public void Migration_LegacyBoolMapsCorrectly(string json, string expectedName)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.True(CursorDisplayModeMigration.TryResolveFromUserRoot(doc.RootElement, out var mode));
        Assert.Equal(expectedName, mode.ToString().ToLowerInvariant());
    }

    [Fact]
    public void Migration_NewKeyPresent_SkipsLegacy()
    {
        using var doc = JsonDocument.Parse(
            """{"cursor_display_mode":"sharp","cursor_motion_dim_enabled":true}""");
        Assert.False(CursorDisplayModeMigration.TryResolveFromUserRoot(doc.RootElement, out _));
    }
}
