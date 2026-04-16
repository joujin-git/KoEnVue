using System.Diagnostics;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Animation;

/// <summary>
/// WM_TIMER 기반 오버레이 애니메이션 상태 머신.
/// Hidden → FadingIn → Holding → FadingOut → Idle 순환.
///
/// <para>
/// 이즈-아웃 큐빅 <c>1-(1-t)^3</c> 보간, 슬라이드 보간, 하이라이트 스케일, TOPMOST 재적용 타이머를
/// 모두 내부에 소유한다. Overlay의 public 표면(렌더, 위치, 알파, 크기, 숨김, TOPMOST 강제)은
/// 생성자 주입 콜백으로만 접근한다 — 엔진은 Overlay 타입을 모른다.
/// </para>
///
/// <para>
/// Stage 4-B 추출: 기존 App/UI/Animation.cs static 상태 머신에서 분리. IME 상태 enum과
/// 비한국어 IME 모드 enum은 엔진에 누출되지 않으며 Dim 여부는
/// <see cref="SetDimMode(bool)"/>로만 전달된다.
/// </para>
///
/// <para>
/// 모든 메서드는 메인 UI 스레드에서만 호출한다.
/// </para>
/// </summary>
public sealed class OverlayAnimator : IDisposable
{
    // ================================================================
    // 상태 모델
    // ================================================================

    private enum AnimPhase { Hidden, FadingIn, Holding, FadingOut, Idle }

    private AnimPhase _phase = AnimPhase.Hidden;
    private byte _currentAlpha;
    private byte _targetAlpha;

    // 페이드 보간
    private long _fadeStartTick;
    private byte _fadeStartAlpha;
    private byte _fadeEndAlpha;
    private int _fadeDurationMs;

    // Hold 타이머
    private int _holdDurationMs;

    // 강조 (HighlightScale → 1.0)
    private bool _highlightActive;
    private long _highlightStartTick;
    private double _highlightStartScale;

    // 슬라이드 보간
    private bool _slideActive;
    private long _slideStartTick;
    private int _slideFromX, _slideFromY, _slideToX, _slideToY;
    private int _slideDurationMs;

    // forceHidden 플래그 (FadingOut 완료 시 Hidden 강제)
    private bool _forceHidden;

    // Dim 모드 (비한국어 IME Dim 판정은 파사드에서 수행 후 이 플래그로만 전달)
    private bool _dimmed;

    // ================================================================
    // 의존성 (생성자 주입)
    // ================================================================

    private readonly IntPtr _hwndTimer;
    private readonly AnimationTimerIds _timerIds;
    private AnimationConfig _config;

    private readonly Action<byte> _onAlphaChange;
    private readonly Action<int, int> _onPositionOffset;
    private readonly Action<int, int, int, int, byte> _onScaledSize;
    private readonly Action _onHide;
    private readonly Action _onForceTopmost;
    private readonly Func<(int w, int h)> _getBaseSize;

    // GetLastPosition — 슬라이드/하이라이트 시점 기준 좌표. 엔진이 Show 후 직접 기억한다.
    private int _lastX;
    private int _lastY;

    // ================================================================
    // 생성자 / Dispose
    // ================================================================

    public OverlayAnimator(
        IntPtr hwndTimer,
        AnimationTimerIds timerIds,
        AnimationConfig initialConfig,
        Action<byte> onAlphaChange,
        Action<int, int> onPositionOffset,
        Action<int, int, int, int, byte> onScaledSize,
        Action onHide,
        Action onForceTopmost,
        Func<(int w, int h)> getBaseSize)
    {
        _hwndTimer = hwndTimer;
        _timerIds = timerIds;
        _config = initialConfig;
        _onAlphaChange = onAlphaChange;
        _onPositionOffset = onPositionOffset;
        _onScaledSize = onScaledSize;
        _onHide = onHide;
        _onForceTopmost = onForceTopmost;
        _getBaseSize = getBaseSize;

        // TOPMOST 재적용 타이머 시작
        if (initialConfig.ForceTopmostIntervalMs > 0)
        {
            User32.SetTimer(_hwndTimer, _timerIds.Topmost,
                (uint)initialConfig.ForceTopmostIntervalMs, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        User32.KillTimer(_hwndTimer, _timerIds.Fade);
        User32.KillTimer(_hwndTimer, _timerIds.Hold);
        User32.KillTimer(_hwndTimer, _timerIds.Highlight);
        User32.KillTimer(_hwndTimer, _timerIds.Slide);
        User32.KillTimer(_hwndTimer, _timerIds.Topmost);
    }

    // ================================================================
    // 설정 / Dim 모드 갱신
    // ================================================================

    /// <summary>
    /// 새 AnimationConfig 스냅샷 적용. <see cref="TriggerShow"/>/<see cref="TriggerHide"/> 진입 시
    /// 파사드가 매번 호출한다. record struct 값 동등성으로 변화 없으면 저비용 경로.
    /// ForceTopmostIntervalMs가 바뀌면 타이머를 재시작한다.
    /// </summary>
    public void UpdateConfig(AnimationConfig config)
    {
        if (_config == config) return;

        bool topmostChanged = _config.ForceTopmostIntervalMs != config.ForceTopmostIntervalMs;
        _config = config;

        if (topmostChanged)
        {
            User32.KillTimer(_hwndTimer, _timerIds.Topmost);
            if (config.ForceTopmostIntervalMs > 0)
            {
                User32.SetTimer(_hwndTimer, _timerIds.Topmost,
                    (uint)config.ForceTopmostIntervalMs, IntPtr.Zero);
            }
        }
    }

    /// <summary>
    /// Dim 모드 (투명도 50% 감소) 플래그 설정. 파사드가 "비한국어 IME + Dim 모드" 조합을
    /// 판정해 이 메서드로만 전달한다. 엔진은 IME 상태 enum과 비한국어 IME 모드 enum을 모른다.
    /// </summary>
    public void SetDimMode(bool dimmed)
    {
        _dimmed = dimmed;
    }

    // ================================================================
    // TriggerShow
    // ================================================================

    /// <summary>
    /// 새 위치/상태로 인디케이터 표시. 파사드가 <c>Overlay.Show(x, y, state)</c>를 호출하기 전/후에
    /// 필요한 작업(prev 좌표 기록, 슬라이드 시작, 하이라이트, 알파 목표 전이)을 수행한다.
    ///
    /// prev 좌표는 파사드가 <c>Overlay.GetLastPosition()</c>으로 먼저 조회하여 넘겨준다.
    ///
    /// <para>
    /// 리턴값: <c>true</c> = 이번 호출이 Hidden 상태에서 전이되었음 (파사드가 초기 SW_SHOW 복원 필요).
    /// <c>false</c> = 이미 가시 상태에서의 갱신.
    /// </para>
    /// </summary>
    public bool TriggerShow(int prevX, int prevY, int newX, int newY, bool highlightTrigger)
    {
        bool wasHidden = _phase == AnimPhase.Hidden;

        // hold 타이머 duration 선택
        _holdDurationMs = _config.AlwaysMode
            ? _config.AlwaysIdleTimeoutMs
            : _config.EventDisplayDurationMs;

        // targetAlpha 계산 (_dimmed 반영)
        _targetAlpha = GetTargetAlpha(active: true);

        // 새 좌표 저장 (GetLastPosition 대체)
        _lastX = newX;
        _lastY = newY;

        if (_phase == AnimPhase.Holding || _phase == AnimPhase.FadingIn)
        {
            User32.KillTimer(_hwndTimer, _timerIds.Hold);
            User32.SetTimer(_hwndTimer, _timerIds.Hold,
                (uint)_holdDurationMs, IntPtr.Zero);

            TryStartSlide(prevX, prevY, newX, newY);

            if (highlightTrigger && _config.ChangeHighlight)
                StartHighlight();

            if (_currentAlpha != _targetAlpha)
            {
                _currentAlpha = _targetAlpha;
                _onAlphaChange(_targetAlpha);
            }
        }
        else if (_phase == AnimPhase.Idle)
        {
            StartFade(_currentAlpha, _targetAlpha, _config.FadeInMs);
            _phase = AnimPhase.FadingIn;
            User32.SetTimer(_hwndTimer, _timerIds.Fade, _config.AnimationFrameMs, IntPtr.Zero);

            TryStartSlide(prevX, prevY, newX, newY);

            if (highlightTrigger && _config.ChangeHighlight)
                StartHighlight();

            if (_currentAlpha != _targetAlpha)
            {
                _currentAlpha = _targetAlpha;
                _onAlphaChange(_targetAlpha);
            }
        }
        else if (_phase == AnimPhase.FadingOut)
        {
            User32.KillTimer(_hwndTimer, _timerIds.Fade);
            _forceHidden = false; // TriggerHide(forceHidden)가 남긴 플래그 초기화
            StartFade(_currentAlpha, _targetAlpha, _config.FadeInMs);
            _phase = AnimPhase.FadingIn;
            User32.SetTimer(_hwndTimer, _timerIds.Fade, _config.AnimationFrameMs, IntPtr.Zero);

            TryStartSlide(prevX, prevY, newX, newY);

            if (highlightTrigger && _config.ChangeHighlight)
                StartHighlight();
        }
        else // Hidden
        {
            if (_config.AnimationEnabled)
            {
                StartFade(0, _targetAlpha, _config.FadeInMs);
                _phase = AnimPhase.FadingIn;
                User32.SetTimer(_hwndTimer, _timerIds.Fade, _config.AnimationFrameMs, IntPtr.Zero);
            }
            else
            {
                _currentAlpha = _targetAlpha;
                _onAlphaChange(_targetAlpha);
                _phase = AnimPhase.Holding;
                User32.SetTimer(_hwndTimer, _timerIds.Hold,
                    (uint)_holdDurationMs, IntPtr.Zero);
            }

            if (highlightTrigger && _config.ChangeHighlight)
                StartHighlight();
        }

        return wasHidden;
    }

    // ================================================================
    // TriggerHide
    // ================================================================

    public void TriggerHide(bool forceHidden)
    {
        // 이미 숨김/유휴 상태면 처리
        if (_phase == AnimPhase.Hidden) return;
        if (_phase == AnimPhase.Idle && !forceHidden) return;

        // 1. 모든 타이머 정리
        User32.KillTimer(_hwndTimer, _timerIds.Fade);
        User32.KillTimer(_hwndTimer, _timerIds.Hold);
        User32.KillTimer(_hwndTimer, _timerIds.Highlight);
        User32.KillTimer(_hwndTimer, _timerIds.Slide);
        _highlightActive = false;
        _slideActive = false;

        // 2. 모드별 분기
        if (_config.AlwaysMode && !forceHidden)
        {
            // Always 모드: IdleOpacity로 페이드 (dim-idle)
            FadeToIdle();
        }
        else
        {
            // OnEvent 모드 또는 forceHidden
            if (_config.AnimationEnabled && _currentAlpha > 0)
            {
                _forceHidden = forceHidden;
                StartFade(_currentAlpha, 0, _config.FadeOutMs);
                _phase = AnimPhase.FadingOut;
                User32.SetTimer(_hwndTimer, _timerIds.Fade, _config.AnimationFrameMs, IntPtr.Zero);
            }
            else
            {
                _onHide();
                _phase = AnimPhase.Hidden;
                _currentAlpha = 0;
            }
        }
    }

    // ================================================================
    // TriggerHighlight (외부에서 강조만 발생시킬 때)
    // ================================================================

    public void TriggerHighlight()
    {
        if (_config.ChangeHighlight)
            StartHighlight();
    }

    // ================================================================
    // WM_TIMER 진입
    // ================================================================

    public void OnWmTimer(nuint timerId)
    {
        if (timerId == _timerIds.Fade)
            HandleFadeTimer();
        else if (timerId == _timerIds.Hold)
            HandleHoldTimer();
        else if (timerId == _timerIds.Highlight)
            HandleHighlightTimer();
        else if (timerId == _timerIds.Slide)
            HandleSlideTimer();
        else if (timerId == _timerIds.Topmost)
            _onForceTopmost();
    }

    // ================================================================
    // 타이머 핸들러
    // ================================================================

    private void HandleFadeTimer()
    {
        double elapsed = GetElapsedMs(_fadeStartTick);
        double ratio = _fadeDurationMs > 0 ? Math.Clamp(elapsed / _fadeDurationMs, 0.0, 1.0) : 1.0;

        byte alpha = (byte)(_fadeStartAlpha + (_fadeEndAlpha - _fadeStartAlpha) * ratio);
        _currentAlpha = alpha;
        _onAlphaChange(alpha);

        if (ratio >= 1.0)
        {
            User32.KillTimer(_hwndTimer, _timerIds.Fade);

            if (_phase == AnimPhase.FadingIn)
            {
                _currentAlpha = _fadeEndAlpha;
                _phase = AnimPhase.Holding;
                User32.SetTimer(_hwndTimer, _timerIds.Hold,
                    (uint)_holdDurationMs, IntPtr.Zero);
            }
            else if (_phase == AnimPhase.FadingOut)
            {
                if (_forceHidden || !_config.AlwaysMode)
                {
                    // OnEvent 모드 또는 forceHidden → 완전 숨김
                    _onHide();
                    _phase = AnimPhase.Hidden;
                    _currentAlpha = 0;
                    _forceHidden = false;
                }
                else
                {
                    // Always 모드 → Idle
                    _currentAlpha = _fadeEndAlpha;
                    _phase = AnimPhase.Idle;
                }
            }
        }
    }

    private void HandleHoldTimer()
    {
        User32.KillTimer(_hwndTimer, _timerIds.Hold);

        if (_config.AlwaysMode)
        {
            // Always 모드: ActiveOpacity → IdleOpacity 페이드 (dim-idle)
            FadeToIdle();
            return;
        }

        // OnEvent 모드: 페이드아웃 → Hidden
        StartFade(_currentAlpha, 0, _config.FadeOutMs);
        _phase = AnimPhase.FadingOut;
        User32.SetTimer(_hwndTimer, _timerIds.Fade, _config.AnimationFrameMs, IntPtr.Zero);
    }

    private void HandleHighlightTimer()
    {
        if (!_highlightActive) return;

        double elapsed = GetElapsedMs(_highlightStartTick);
        double ratio = _config.HighlightDurationMs > 0
            ? Math.Clamp(elapsed / _config.HighlightDurationMs, 0.0, 1.0) : 1.0;

        double scale = _highlightStartScale + (1.0 - _highlightStartScale) * ratio;

        var (baseW, baseH) = _getBaseSize();

        int newW = (int)Math.Round(baseW * scale);
        int newH = (int)Math.Round(baseH * scale);
        int newX = _lastX - (newW - baseW) / 2;
        int newY = _lastY - (newH - baseH) / 2;

        _onScaledSize(newX, newY, newW, newH, _currentAlpha);

        if (ratio >= 1.0)
        {
            User32.KillTimer(_hwndTimer, _timerIds.Highlight);
            _highlightActive = false;

            // 원래 크기 복원
            _onScaledSize(_lastX, _lastY, baseW, baseH, _currentAlpha);
        }
    }

    private void HandleSlideTimer()
    {
        if (!_slideActive) return;

        double elapsed = GetElapsedMs(_slideStartTick);
        double t = _slideDurationMs > 0 ? Math.Clamp(elapsed / _slideDurationMs, 0.0, 1.0) : 1.0;

        // ease-out cubic: 1 - (1-t)^3
        double eased = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);

        int x = _slideFromX + (int)Math.Round((_slideToX - _slideFromX) * eased);
        int y = _slideFromY + (int)Math.Round((_slideToY - _slideFromY) * eased);

        _onPositionOffset(x, y);

        if (t >= 1.0)
        {
            User32.KillTimer(_hwndTimer, _timerIds.Slide);
            _slideActive = false;
            // 최종 위치 보정
            _onPositionOffset(_slideToX, _slideToY);
        }
    }

    // ================================================================
    // 내부 헬퍼
    // ================================================================

    /// <summary>
    /// Always 모드 dim-idle 전이: 현재 alpha → IdleOpacity 페이드.
    /// 애니메이션 비활성이거나 alpha가 이미 일치하면 즉시 전이.
    /// FadingOut 완료 핸들러의 Always 분기가 Idle로 최종 전이한다.
    /// </summary>
    private void FadeToIdle()
    {
        byte idleAlpha = GetTargetAlpha(active: false);
        if (_currentAlpha != idleAlpha && _config.AnimationEnabled)
        {
            StartFade(_currentAlpha, idleAlpha, _config.FadeOutMs);
            _phase = AnimPhase.FadingOut;
            User32.SetTimer(_hwndTimer, _timerIds.Fade, _config.AnimationFrameMs, IntPtr.Zero);
        }
        else
        {
            if (_currentAlpha != idleAlpha)
            {
                _currentAlpha = idleAlpha;
                _onAlphaChange(idleAlpha);
            }
            _phase = AnimPhase.Idle;
        }
    }

    private byte GetTargetAlpha(bool active)
    {
        double raw;

        if (_config.AlwaysMode)
            raw = active ? _config.ActiveOpacity : _config.IdleOpacity;
        else
            raw = _config.Opacity;

        // Dim 모드 (파사드가 SetDimMode로 주입)
        if (_dimmed)
            raw *= _config.DimOpacityFactor;

        return (byte)(raw * 255);
    }

    private void StartFade(byte from, byte to, int durationMs)
    {
        _fadeStartAlpha = from;
        _fadeEndAlpha = to;
        _fadeDurationMs = durationMs;
        _fadeStartTick = Stopwatch.GetTimestamp();
    }

    private void StartHighlight()
    {
        _highlightActive = true;
        _highlightStartScale = _config.HighlightScale;
        _highlightStartTick = Stopwatch.GetTimestamp();
        User32.SetTimer(_hwndTimer, _timerIds.Highlight, _config.AnimationFrameMs, IntPtr.Zero);
    }

    private static double GetElapsedMs(long startTick)
    {
        long now = Stopwatch.GetTimestamp();
        return (now - startTick) * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// prev → new 위치 차이가 있고 슬라이드가 켜져 있으면 prev로 즉시 복원 후 슬라이드 시작.
    /// DWM VSync 내 같은 메시지 핸들러에서 Show→UpdatePosition 연속 호출이므로
    /// 중간 위치는 화면에 표시되지 않는다.
    /// </summary>
    private void TryStartSlide(int prevX, int prevY, int newX, int newY)
    {
        if (_config.SlideAnimation && _config.SlideSpeedMs > 0
            && (prevX != newX || prevY != newY))
        {
            // 이전 위치로 즉시 복원 (같은 프레임 — DWM이 수집 전)
            _onPositionOffset(prevX, prevY);
            StartSlide(prevX, prevY, newX, newY, _config.SlideSpeedMs);
        }
    }

    private void StartSlide(int fromX, int fromY, int toX, int toY, int durationMs)
    {
        _slideActive = true;
        _slideFromX = fromX;
        _slideFromY = fromY;
        _slideToX = toX;
        _slideToY = toY;
        _slideDurationMs = durationMs;
        _slideStartTick = Stopwatch.GetTimestamp();
        User32.SetTimer(_hwndTimer, _timerIds.Slide, _config.AnimationFrameMs, IntPtr.Zero);
    }
}
