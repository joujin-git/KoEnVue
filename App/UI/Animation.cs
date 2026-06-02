using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Animation;
using KoEnVue.Core.Native;

namespace KoEnVue.App.UI;

/// <summary>
/// WM_TIMER 기반 애니메이션 상태 머신의 App 레이어 파사드.
/// 실제 상태 머신은 <see cref="OverlayAnimator"/>가 소유한다 (Core/Animation).
///
/// <para>
/// 이 파사드의 책임:
/// 1. AppConfig → <see cref="AnimationConfig"/> 변환
/// 2. ImeState/NonKoreanImeMode 분기 (Hide/Dim 판정) — Core에 누출되지 않도록 앱 레이어에서 처리
/// 3. Overlay 타입 접근 (엔진은 Overlay를 모른다 — 콜백으로 위임받음)
/// 4. <see cref="Overlay.Show(int, int, ImeState, AppConfig)"/> 및 <see cref="Overlay.UpdateColor(ImeState, AppConfig)"/> 호출
///    (ImeState 와 per-app resolved AppConfig 가 필요한 호출은 파사드 책임)
/// </para>
///
/// <para>
/// 모든 호출은 메인 스레드에서만 수행. Program.cs 호출 표현식은 Stage 3 이후 유지된다.
/// </para>
/// </summary>
internal static class Animation
{
    private static OverlayAnimator? _animator;
    private static IntPtr _hwndOverlay;

    // ================================================================
    // 초기화 / 해제
    // ================================================================

    public static void Initialize(IntPtr hwndMain, IntPtr hwndOverlay, AppConfig config)
    {
        _hwndOverlay = hwndOverlay;

        var timerIds = new AnimationTimerIds(
            Fade: AppMessages.TIMER_ID_FADE,
            Hold: AppMessages.TIMER_ID_HOLD,
            Highlight: AppMessages.TIMER_ID_HIGHLIGHT,
            Topmost: AppMessages.TIMER_ID_TOPMOST,
            Slide: AppMessages.TIMER_ID_SLIDE);

        _animator = new OverlayAnimator(
            hwndTimer: hwndMain,
            timerIds: timerIds,
            initialConfig: BuildAnimationConfig(config),
            onAlphaChange: Overlay.UpdateAlpha,
            onPositionOffset: Overlay.UpdatePosition,
            onTrackPosition: Overlay.TrackPosition,
            onScaledSize: Overlay.UpdateScaledSize,
            onHide: Overlay.Hide,
            onForceTopmost: Overlay.ForceTopmost,
            getBaseSize: Overlay.GetBaseSize);
    }

    public static void Dispose()
    {
        _animator?.Dispose();
        _animator = null;
    }

    // ================================================================
    // TriggerShow — ImeState/NonKoreanImeMode 분기 후 엔진에 위임
    // ================================================================

    public static void TriggerShow(int x, int y,
        ImeState state, AppConfig config, bool imeChanged)
    {
        if (_animator is null) return;

        // NonKoreanImeMode.Hide 가드 — 엔진은 ImeState를 모르므로 파사드가 처리
        if (state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Hide)
        {
            TriggerHide(config, forceHidden: true);
            return;
        }

        // AnimationConfig 스냅샷 갱신 (매 호출, 엔진 내부에서 값 동등성 비교)
        _animator.UpdateConfig(BuildAnimationConfig(config));

        // Dim 모드 판정 (NonKorean + Dim) — 엔진에는 bool로만 전달
        bool dimmed = state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Dim;
        _animator.SetDimMode(dimmed);

        // prev 좌표 스냅샷 (엔진 호출 전 Overlay.Show가 _lastX/_lastY를 갱신하기 때문에 먼저 저장)
        var (prevX, prevY) = Overlay.GetLastPosition();

        // 렌더링 (ImeState 필요 — 파사드 책임)
        // PR-13: per-app resolved AppConfig 를 명시 전달 — `Overlay._config` 글로벌 의존 제거.
        Overlay.Show(x, y, state, config);

        // 상태 머신 전이 — wasHidden 리턴으로 Hidden→visible 전이 여부 판정
        bool wasHidden = _animator.TriggerShow(prevX, prevY, x, y, highlightTrigger: imeChanged);

        // Hidden에서 막 전이했다면 Hide()의 SW_HIDE를 SW_SHOW로 복원.
        // Hidden 분기에서는 Overlay.Show가 이미 새 state로 비트맵을 렌더했으므로 UpdateColor 불필요 (원본과 동등).
        if (wasHidden)
        {
            User32.ShowWindow(_hwndOverlay, Win32Constants.SW_SHOW);
        }
        else if (imeChanged)
        {
            // 비-Hidden 분기에서 imeChanged면 UpdateColor로 비트맵 갱신 (원본 Holding/Idle/FadingOut 경로).
            Overlay.UpdateColor(state, config);
        }
    }

    // ================================================================
    // TriggerHide
    // ================================================================

    public static void TriggerHide(AppConfig config, bool forceHidden = false)
    {
        if (_animator is null) return;

        _animator.UpdateConfig(BuildAnimationConfig(config));
        _animator.TriggerHide(forceHidden);
    }

    // ================================================================
    // HandleTimer — Program.cs WM_TIMER 핸들러에서 호출
    // ================================================================

    public static void HandleTimer(nuint timerId, AppConfig config)
    {
        _ = config; // 엔진이 _config 스냅샷을 소유하므로 여기서는 사용하지 않음 (이슈 B4)
        _animator?.OnWmTimer(timerId);
    }

    // ================================================================
    // AppConfig → AnimationConfig 변환
    // ================================================================

    /// <summary>
    /// AppConfig → 엔진 <see cref="AnimationConfig"/> 스냅샷. 트레이 "애니메이션 사용"
    /// (<c>AnimationEnabled</c>) 이 fade(엔진이 직접 게이팅) 뿐 아니라 highlight·slide 까지 끄는
    /// <b>마스터 스위치</b>가 되도록, 여기서 <c>ChangeHighlight</c>/<c>SlideAnimation</c> 을
    /// <c>AnimationEnabled &amp;&amp;</c> 로 합성한다 (PR-22 — 라벨·PRD·config-reference 의 "전체 on/off"
    /// 의도와 코드 일치, 마스터 의미의 단일 진실원). <c>internal</c>: 합성 동작을 단위 테스트
    /// (<c>AnimationFacadeTests</c>) 로 박제하기 위함 — InternalsVisibleTo.
    /// </summary>
    internal static AnimationConfig BuildAnimationConfig(AppConfig config) => new(
        AnimationEnabled: config.AnimationEnabled,
        AlwaysMode: config.DisplayMode == DisplayMode.Always,
        FadeInMs: config.FadeInMs,
        FadeOutMs: config.FadeOutMs,
        AlwaysIdleTimeoutMs: config.AlwaysIdleTimeoutMs,
        EventDisplayDurationMs: config.EventDisplayDurationMs,
        Opacity: config.Opacity,
        IdleOpacity: config.IdleOpacity,
        ActiveOpacity: config.ActiveOpacity,
        ChangeHighlight: config.AnimationEnabled && config.ChangeHighlight,   // 마스터 게이팅 (PR-22)
        HighlightScale: config.HighlightScale,
        HighlightDurationMs: config.HighlightDurationMs,
        SlideAnimation: config.AnimationEnabled && config.SlideAnimation,    // 마스터 게이팅 (PR-22)
        SlideSpeedMs: config.SlideSpeedMs,
        ForceTopmostIntervalMs: config.Advanced.ForceTopmostIntervalMs,
        AnimationFrameMs: DefaultConfig.AnimationFrameMs,
        DimOpacityFactor: DefaultConfig.DimOpacityFactor);
}
