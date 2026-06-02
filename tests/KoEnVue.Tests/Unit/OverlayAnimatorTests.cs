using System;
using System.Collections.Generic;
using KoEnVue.Core.Animation;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// OverlayAnimator 의 slide + highlight 합성 (종합 감사 ⑩ 후속). 강조(스케일 팝)가 진행 중이면
/// slide 는 현재 위치를 추적만 하고(blit 없이), 강조가 그 위치를 중심으로 한 번만 그린다. 이로써
/// 두 트랙이 같은 레이어드 윈도우의 위치/크기를 16ms 간격으로 다투지 않는다(경합 시 인디가
/// 찢기며 사라지던 결함의 회귀 방지).
///
/// 주의: TriggerShow 의 prev/new 좌표는 작은 값(≤200)이라 단일 모니터(primary) 안에 들어가므로
/// IsSameMonitor 가 true → slide 가 시작된다(모니터 간이면 slide 생략).
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
    public void Slide_DuringHighlight_TracksPositionWithoutBlit()
    {
        var posSpy = new List<(int X, int Y)>();
        var trackSpy = new List<(int X, int Y)>();
        var animator = MakeAnimator(slide: true, highlight: true,
            onPositionOffset: (x, y) => posSpy.Add((x, y)),
            onTrackPosition: (x, y) => trackSpy.Add((x, y)));

        // Hidden → Holding (AnimationEnabled=false 라 페이드 없이 즉시 전이)
        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);

        // IME 전환(highlight) + 위치 변경 → slide+highlight 합성 동시 시작
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: true);

        // 합성: slide 타이머는 강조 진행 중 위치만 추적하고 직접 blit 하지 않는다(강조가 그림).
        posSpy.Clear();
        trackSpy.Clear();
        animator.OnWmTimer(SlideId);
        Assert.NotEmpty(trackSpy);   // 위치는 추적됨
        Assert.Empty(posSpy);        // 직접 blit 은 안 함 (경합 방지)
    }

    [Fact]
    public void Slide_WithoutHighlight_BlitsPosition()
    {
        var posSpy = new List<(int X, int Y)>();
        var animator = MakeAnimator(slide: true, highlight: true,
            onPositionOffset: (x, y) => posSpy.Add((x, y)));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);

        // 위치만 변경, IME 전환 없음 → 강조 없음 → slide 가 직접 위치 blit
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: false);

        posSpy.Clear();
        animator.OnWmTimer(SlideId);
        Assert.NotEmpty(posSpy);     // slide 가 직접 보간 좌표를 emit
    }

    [Fact]
    public void Highlight_RunsRegardlessOfSlide()
    {
        var scaledSpy = new List<(int W, int H)>();
        var animator = MakeAnimator(slide: true, highlight: true,
            onScaledSize: (_, _, w, h, _) => scaledSpy.Add((w, h)));

        animator.TriggerShow(0, 0, 100, 100, highlightTrigger: false);
        animator.TriggerShow(100, 100, 200, 200, highlightTrigger: true);

        // 합성 중에도 강조 타이머는 정상 동작(위치+크기를 그린다).
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
        Action<int, int>? onTrackPosition = null,
        Action<int, int, int, int, byte>? onScaledSize = null)
    {
        var ids = new AnimationTimerIds(FadeId, HoldId, HighlightId, TopmostId, SlideId);
        return new OverlayAnimator(
            IntPtr.Zero, ids, MakeConfig(slide, highlight),
            onAlphaChange: _ => { },
            onPositionOffset: onPositionOffset ?? ((_, _) => { }),
            onTrackPosition: onTrackPosition ?? ((_, _) => { }),
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
