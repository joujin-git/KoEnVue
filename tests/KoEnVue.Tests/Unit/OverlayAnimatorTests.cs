using System;
using System.Collections.Generic;
using KoEnVue.Core.Animation;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// OverlayAnimator 의 slide + highlight 트랙 경합 회피 (종합 감사 ⑩).
/// 강조(스케일 팝)가 진행 중이거나 시작될 예정이면 slide 를 보류해, 두 트랙이 같은
/// 레이어드 윈도우의 위치/크기를 16ms 간격으로 다투지 않도록 한 동작을 박제한다.
/// </summary>
public class OverlayAnimatorTests
{
    // App 이 소유하는 WM_TIMER ID — 테스트에서는 임의 고정값으로 주입.
    private static readonly nuint FadeId = 1;
    private static readonly nuint HoldId = 2;
    private static readonly nuint HighlightId = 3;
    private static readonly nuint TopmostId = 4;
    private static readonly nuint SlideId = 5;

    [Fact]
    public void TriggerShow_WillHighlight_SuppressesSlide()
    {
        var posSpy = new List<(int X, int Y)>();
        var animator = MakeAnimator(slide: true, highlight: true,
            onPositionOffset: (x, y) => posSpy.Add((x, y)));

        // Hidden → Holding (AnimationEnabled=false 라 페이드 없이 즉시 전이)
        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);

        // Holding 에서 IME 전환(highlight) + 위치 변경 → willHighlight=true → slide 보류
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: true);

        // slide 가 보류됐으면 Slide 타이머는 비활성 → 핸들러가 보간 좌표를 내지 않는다.
        posSpy.Clear();
        animator.OnWmTimer(SlideId);
        Assert.Empty(posSpy);
    }

    [Fact]
    public void TriggerShow_NoHighlight_RunsSlide()
    {
        var posSpy = new List<(int X, int Y)>();
        var animator = MakeAnimator(slide: true, highlight: true,
            onPositionOffset: (x, y) => posSpy.Add((x, y)));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);

        // 위치만 변경, IME 전환 없음 → willHighlight=false → slide 정상 시작
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: false);

        // slide 활성 → Slide 타이머가 보간 좌표를 emit 한다.
        posSpy.Clear();
        animator.OnWmTimer(SlideId);
        Assert.NotEmpty(posSpy);
    }

    [Fact]
    public void TriggerShow_WillHighlight_StillRunsHighlight()
    {
        var scaledSpy = new List<(int W, int H)>();
        var animator = MakeAnimator(slide: true, highlight: true,
            onScaledSize: (_, _, w, h, _) => scaledSpy.Add((w, h)));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: true);

        // slide 보류가 highlight 자체를 죽이면 안 된다 — Highlight 타이머는 정상 동작.
        scaledSpy.Clear();
        animator.OnWmTimer(HighlightId);
        Assert.NotEmpty(scaledSpy);
    }

    // ================================================================
    // 헬퍼
    // ================================================================

    private static OverlayAnimator MakeAnimator(
        bool slide, bool highlight,
        Action<int, int>? onPositionOffset = null,
        Action<int, int, int, int, byte>? onScaledSize = null)
    {
        var ids = new AnimationTimerIds(FadeId, HoldId, HighlightId, TopmostId, SlideId);
        return new OverlayAnimator(
            IntPtr.Zero, ids, MakeConfig(slide, highlight),
            onAlphaChange: _ => { },
            onPositionOffset: onPositionOffset ?? ((_, _) => { }),
            onScaledSize: onScaledSize ?? ((_, _, _, _, _) => { }),
            onHide: () => { },
            onForceTopmost: () => { },
            getBaseSize: () => (40, 40));
    }

    private static AnimationConfig MakeConfig(bool slide, bool highlight) => new(
        AnimationEnabled: false,        // Hidden→즉시 Holding (페이드 타이머 우회, 동기 전이)
        AlwaysMode: false,
        FadeInMs: 150,
        FadeOutMs: 150,
        AlwaysIdleTimeoutMs: 1000,
        EventDisplayDurationMs: 1000,
        Opacity: 0.9,
        IdleOpacity: 0.5,
        ActiveOpacity: 0.9,
        ChangeHighlight: highlight,
        HighlightScale: 1.3,
        HighlightDurationMs: 300,
        SlideAnimation: slide,
        SlideSpeedMs: 100,
        ForceTopmostIntervalMs: 0,      // TopmostWatchdog 비활성 (타이머 미등록)
        AnimationFrameMs: 16,
        DimOpacityFactor: 0.5);
}
