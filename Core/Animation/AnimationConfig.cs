namespace KoEnVue.Core.Animation;

/// <summary>
/// OverlayAnimator 엔진의 불변 설정 스냅샷.
/// AppConfig → AnimationConfig 변환은 App 레이어 파사드(Animation.cs)가 담당.
/// Core는 App의 레코드를 모른다 (P6).
/// </summary>
/// <remarks>
/// IME 상태 enum과 비한국어 IME 모드 enum은 의도적으로 포함되지 않는다.
/// Dim 여부는 파사드가 <see cref="OverlayAnimator.SetDimMode(bool)"/>로 주입한다.
/// </remarks>
public readonly record struct AnimationConfig(
    bool AnimationEnabled,
    bool AlwaysMode,
    int FadeInMs,
    int FadeOutMs,
    int AlwaysIdleTimeoutMs,
    int EventDisplayDurationMs,
    double Opacity,
    double IdleOpacity,
    double ActiveOpacity,
    bool ChangeHighlight,
    double HighlightScale,
    int HighlightDurationMs,
    bool SlideAnimation,
    int SlideSpeedMs,
    int ForceTopmostIntervalMs,
    uint AnimationFrameMs,
    double DimOpacityFactor);

/// <summary>
/// 5개 WM_TIMER ID 묶음. App이 소유하는 ID를 엔진에 주입한다.
/// Core는 ID 값을 상수로 박지 않는다 — 다른 Core 모듈과의 충돌 방지.
/// </summary>
public readonly record struct AnimationTimerIds(
    nuint Fade,
    nuint Hold,
    nuint Highlight,
    nuint Topmost,
    nuint Slide);
