using KoEnVue.App.Config;
using KoEnVue.App.UI;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>PR-29 이동 딤 — settle·균일 안개 알파 회귀 가드.</summary>
public class CursorMotionDimTests
{
    [Fact]
    public void AdvanceDimActive_Moving_EntersImmediately()
    {
        int still = 5;
        bool dim = CursorMotionDim.AdvanceDimActive(ref still, moving: true, wasDimActive: false, settlePolls: 3);
        Assert.True(dim);
        Assert.Equal(0, still);
    }

    [Fact]
    public void AdvanceDimActive_ExitRequiresSettlePolls()
    {
        int still = 0;
        bool dim = true;
        dim = CursorMotionDim.AdvanceDimActive(ref still, moving: false, wasDimActive: dim, settlePolls: 3);
        Assert.True(dim);
        Assert.Equal(1, still);
        dim = CursorMotionDim.AdvanceDimActive(ref still, moving: false, wasDimActive: dim, settlePolls: 3);
        Assert.True(dim);
        Assert.Equal(2, still);
        dim = CursorMotionDim.AdvanceDimActive(ref still, moving: false, wasDimActive: dim, settlePolls: 3);
        Assert.False(dim);
        Assert.Equal(0, still);
    }

    [Fact]
    public void RingAlphas_FogNearlyUniformAcrossThreeRings()
    {
        var dim = CursorMotionDim.RingAlphas(true, true, false, DefaultConfig.CursorMotionAlpha,
            DefaultConfig.CursorMotionRingInnerFactor,
            DefaultConfig.CursorMotionRingMiddleFactor,
            DefaultConfig.CursorMotionRingOuterFactor,
            DefaultConfig.MinCursorMotionAlpha);
        Assert.Equal(DefaultConfig.CursorMotionAlpha, dim.Inner);
        // 안개: 세 원 알파가 크게 갈리지 않음 (바깥이 안쪽의 80% 이상)
        Assert.True(dim.Outer >= dim.Inner * 0.80);
        Assert.True(dim.Middle >= dim.Inner * 0.90);
    }
}
