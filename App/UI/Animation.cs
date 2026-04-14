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
/// 4. <see cref="Overlay.Show(int, int, ImeState)"/> 및 <see cref="Overlay.UpdateColor(ImeState)"/> 호출
///    (ImeState가 필요한 호출은 파사드 책임)
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
        Overlay.Show(x, y, state);

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
            Overlay.UpdateColor(state);
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
    // 설정 변경 훅 (Program.cs는 호출하지 않음 — TriggerShow/TriggerHide가 진입 시 UpdateConfig)
    // ================================================================

    /// <summary>
    /// 설정 변경 시 엔진의 AnimationConfig 스냅샷 갱신.
    /// 현재 Program.cs는 이 메서드를 호출하지 않는다. <see cref="TriggerShow"/>/<see cref="TriggerHide"/>가
    /// 진입 시마다 자동으로 갱신한다. 향후 외부 호출이 필요할 경우를 대비한 공개 API.
    /// </summary>
    public static void HandleConfigChanged(AppConfig config)
    {
        _animator?.UpdateConfig(BuildAnimationConfig(config));
    }

    // ================================================================
    // AppConfig → AnimationConfig 변환
    // ================================================================

    private static AnimationConfig BuildAnimationConfig(AppConfig config) => new(
        AnimationEnabled: config.AnimationEnabled,
        AlwaysMode: config.DisplayMode == DisplayMode.Always,
        FadeInMs: config.FadeInMs,
        FadeOutMs: config.FadeOutMs,
        AlwaysIdleTimeoutMs: config.AlwaysIdleTimeoutMs,
        EventDisplayDurationMs: config.EventDisplayDurationMs,
        Opacity: config.Opacity,
        IdleOpacity: config.IdleOpacity,
        ActiveOpacity: config.ActiveOpacity,
        ChangeHighlight: config.ChangeHighlight,
        HighlightScale: config.HighlightScale,
        HighlightDurationMs: config.HighlightDurationMs,
        SlideAnimation: config.SlideAnimation,
        SlideSpeedMs: config.SlideSpeedMs,
        ForceTopmostIntervalMs: config.Advanced.ForceTopmostIntervalMs,
        AnimationFrameMs: DefaultConfig.AnimationFrameMs,
        DimOpacityFactor: DefaultConfig.DimOpacityFactor);
}
