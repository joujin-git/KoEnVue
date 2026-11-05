using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.Messaging;
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

    /// <param name="onHidden">
    /// 애니메이터가 fade-out 을 끝내고 오버레이를 실제로 숨긴 직후 호출된다. 호출자(Program)가
    /// 자기 가시 상태(<c>_indicatorVisible</c>)를 함께 내리기 위한 훅 — 이것이 없으면 애니메이션이
    /// 스스로 숨긴 경우에만 플래그가 <c>true</c> 로 남아, 메인·감지 로직이 "보이는 중" 으로 오판한 채
    /// 영구 불일치에 빠진다 (AUDIT-2026-07-30 §N-34).
    /// </param>
    public static void Initialize(IntPtr hwndMain, IntPtr hwndOverlay, AppConfig config,
        Action? onHidden = null)
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
            onHide: () =>
            {
                Overlay.Hide();
                onHidden?.Invoke();
            },
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

    /// <summary>
    /// 배지 표시를 요청한다.
    /// </summary>
    /// <returns>
    /// <b>실제로 표시했으면 true.</b> NonKorean + <see cref="NonKoreanImeMode.Hide"/> 가드로 숨김
    /// 처리했거나 엔진이 없으면 false.
    ///
    /// <para>
    /// 호출자는 이 <b>결과</b>로 가시성 플래그를 세워야 한다 — 호출 전에 미리 true 로 세우면
    /// 아래 Hide 가드 경로에서 화면과 어긋난 채 박제된다. 그 경로는 <c>TriggerHide</c> 로 위임하는데,
    /// 애니메이터가 이미 <c>Hidden</c> phase 면 <c>OverlayAnimator.TriggerHide</c> 가 첫 줄에서
    /// 즉시 return 하므로 <c>onHide</c> → <c>onHidden</c> 훅(= §N-34 가 놓은 안전망)이 발화하지
    /// 않는다. <c>_phase</c> 초기값이 <c>Hidden</c> 이라 <b>부팅 후 첫 NonKorean 알림에서 바로</b>
    /// 성립한다 (bug-hunt 2026-08-02 확정 #7·#17·#25·#37).
    /// </para>
    /// </returns>
    public static bool TriggerShow(int x, int y,
        ImeState state, AppConfig config, bool imeChanged)
    {
        if (_animator is null) return false;

        // NonKoreanImeMode.Hide 가드 — 엔진은 ImeState를 모르므로 파사드가 처리
        if (state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Hide)
        {
            TriggerHide(config, forceHidden: true);
            return false;
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

        return true;
    }

    /// <summary>
    /// 보이는 배지를 새 스타일·좌표로 <b>제자리에서</b> 갱신한다 — 리로드·시스템 색·디스플레이·DPI·전원 복귀용. 표시 이벤트가
    /// 아니므로 애니메이션 단계와 타이머를 되돌리지 않는다(<see cref="OverlayAnimator.Refresh"/>, bug-hunt 2026-11-05).
    /// </summary>
    /// <returns>갱신 뒤에도 보이는 단계면 true — 호출자가 가시 플래그로 쓴다.</returns>
    public static bool Refresh(int x, int y, ImeState state, AppConfig config)
    {
        if (_animator is null || !_animator.IsVisiblePhase) return false;

        // NonKoreanImeMode.Hide 가드 — TriggerShow 와 같은 판정 (비한국어 숨김으로 바뀐 경우).
        if (state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Hide)
        {
            TriggerHide(config, forceHidden: true);
            return false;
        }

        _animator.UpdateConfig(BuildAnimationConfig(config));
        _animator.SetDimMode(state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Dim);

        // 갱신 뒤에도 강조·슬라이드가 이어지는가 — 목적지가 바뀌면 애니메이터가 옛 슬라이드를 끊는다(아래 Refresh). 그리기를
        // 먼저 하고 애니메이터를 나중에 갱신해야 투명도 변경 프레임이 옛 모양을 올리지 않는다.
        bool trackContinues = _animator.IsHighlightActive || _animator.IsSlidingTo(x, y);
        if (trackContinues)
        {
            // 진행 중인 강조·슬라이드가 다음 프레임에 올린다 — 여기서 기본 크기·목적지로 한 번 블리트하면 팝 도중 한 프레임
            // 줄었다 커지거나 슬라이드 중 목적지로 튀었다 돌아왔다(릴리즈 리뷰 2026-11-05, RefreshColor 와 같은 이유).
            // DIB 만 새 스타일로 그리고, 엔진 좌표는 새 목적지로 맞춰 둔다(트랙이 끝난 뒤 페이드 프레임이 쓰는 기준 — 슬라이드가
            // 계속되면 다음 프레임이 보간 좌표로 덮는다).
            Overlay.Paint(state, config);
            Overlay.TrackPosition(x, y);
        }
        else
        {
            // 새 스타일·좌표로 다시 그린다 (현재 투명도 그대로).
            Overlay.Show(x, y, state, config);
        }

        // 애니메이터가 투명도 목표를 새 설정에 맞추고, 목적지가 바뀌었으면 옛 슬라이드를 끊는다.
        _animator.Refresh(x, y);
        return _animator.IsVisiblePhase;
    }

    /// <summary>
    /// 진행 중인 슬라이드·강조 팝을 끝내고 애니메이터 기준 좌표를 맞춘다 — 배지 드래그의 시작·끝에서 부른다
    /// (<see cref="OverlayAnimator.CancelMotion"/>).
    /// </summary>
    public static void CancelMotion(int x, int y) => _animator?.CancelMotion(x, y);

    /// <summary>
    /// 표시 중인 배지의 색·모양만 갱신(CapsLock 토글 등). 강조 팝 진행 중이면 그리기만 하고 블리트는 다음 강조
    /// 프레임에 맡긴다 — <see cref="Overlay.UpdateColor"/> 는 기본 크기로 블리트해 팝 도중 한 프레임 줄었다가 다시
    /// 커지는 떨림이 났다(bug-hunt 2026-11-04 확정 #30, CapsLock 테두리로 토글마다 다시 그리게 되며 눈에 띔).
    /// </summary>
    public static void RefreshColor(ImeState state, AppConfig config)
    {
        if (_animator is { IsHighlightActive: true })
            Overlay.Paint(state, config);
        else
            Overlay.UpdateColor(state, config);
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
