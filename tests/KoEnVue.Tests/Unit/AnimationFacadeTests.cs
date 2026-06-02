using KoEnVue.App.Models;
using KoEnVue.App.UI;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="Animation.BuildAnimationConfig"/> 의 "애니메이션 사용" 마스터 게이팅 회귀 가드 (PR-22).
/// <c>animation_enabled</c>(<see cref="AppConfig.AnimationEnabled"/>) 가 fade 뿐 아니라 highlight·slide
/// 까지 끄는 마스터 스위치임을 파사드 합성 단계에서 박제한다 — OFF 면 ChangeHighlight/SlideAnimation
/// 이 false 로 합성되고(AND), ON 이면 개별 토글 값을 그대로 보존한다.
/// </summary>
public class AnimationFacadeTests
{
    [Fact]
    public void BuildAnimationConfig_MasterOff_GatesHighlightAndSlide()
    {
        var cfg = new AppConfig() with
        {
            AnimationEnabled = false,
            ChangeHighlight = true,
            SlideAnimation = true,
        };
        var ac = Animation.BuildAnimationConfig(cfg);
        Assert.False(ac.ChangeHighlight);   // 마스터 OFF → 강조 게이팅
        Assert.False(ac.SlideAnimation);    // 마스터 OFF → 슬라이드 게이팅
    }

    [Fact]
    public void BuildAnimationConfig_MasterOn_PreservesIndividualToggles()
    {
        var cfg = new AppConfig() with
        {
            AnimationEnabled = true,
            ChangeHighlight = true,
            SlideAnimation = true,
        };
        var ac = Animation.BuildAnimationConfig(cfg);
        Assert.True(ac.ChangeHighlight);
        Assert.True(ac.SlideAnimation);
    }

    [Fact]
    public void BuildAnimationConfig_MasterOn_IndividualOffStaysOff()
    {
        // 합성은 AND — 마스터 ON 이어도 개별 토글 OFF 는 OFF 유지 (켜지 않는다).
        var cfg = new AppConfig() with
        {
            AnimationEnabled = true,
            ChangeHighlight = false,
            SlideAnimation = false,
        };
        var ac = Animation.BuildAnimationConfig(cfg);
        Assert.False(ac.ChangeHighlight);
        Assert.False(ac.SlideAnimation);
    }

    [Fact]
    public void BuildAnimationConfig_PassesAnimationEnabledThrough()
    {
        // fade 게이팅은 OverlayAnimator 가 AnimationEnabled 로 직접 수행하므로 플래그 자체는 보존.
        Assert.True(Animation.BuildAnimationConfig(new AppConfig() with { AnimationEnabled = true }).AnimationEnabled);
        Assert.False(Animation.BuildAnimationConfig(new AppConfig() with { AnimationEnabled = false }).AnimationEnabled);
    }
}
