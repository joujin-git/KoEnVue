using KoEnVue.App.Config;
using KoEnVue.App.UI;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>PR-29/30 이동 딤 — settle·균일 안개 알파 회귀 가드.</summary>
public class CursorMotionDimTests
{
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
        // AlwaysShow 딤은 저속에도 민감, 숨김 모드는 과민 방지용으로 더 큼 (PR-30).
        Assert.True(DefaultConfig.CursorMotionDimThresholdPx < DefaultConfig.CursorMotionThresholdPx);
        Assert.Equal(1, DefaultConfig.CursorMotionDimThresholdPx);
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
