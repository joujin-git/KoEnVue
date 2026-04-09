using System.Diagnostics;
using KoEnVue.Config;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.UI;

/// <summary>
/// WM_TIMER 기반 애니메이션 상태 머신.
/// Overlay의 public API를 직접 호출 (단방향 의존).
/// 모든 호출은 메인 스레드에서만 수행.
/// </summary>
internal static class Animation
{
    // ================================================================
    // 상태 모델
    // ================================================================

    private enum AnimPhase { Hidden, FadingIn, Holding, FadingOut, Idle }

    private static AnimPhase _phase = AnimPhase.Hidden;
    private static byte _currentAlpha;
    private static byte _targetAlpha;

    // 페이드 보간
    private static long _fadeStartTick;
    private static byte _fadeStartAlpha;
    private static byte _fadeEndAlpha;
    private static int _fadeDurationMs;

    // Hold 타이머
    private static int _holdDurationMs;

    // 강조 (1.3x → 1.0)
    private static bool _highlightActive;
    private static long _highlightStartTick;
    private static double _highlightStartScale;

    // 슬라이드 보간
    private static bool _slideActive;
    private static long _slideStartTick;
    private static int _slideFromX, _slideFromY, _slideToX, _slideToY;
    private static int _slideDurationMs;

    // forceHidden 플래그 (FadingOut 완료 시 Hidden 강제)
    private static bool _forceHidden;

    // Dim 모드 alpha 계산용 IME 상태
    private static ImeState _currentState;

    // 윈도우 핸들
    private static IntPtr _hwndMain;
    private static IntPtr _hwndOverlay;

    // ================================================================
    // 초기화 / 해제
    // ================================================================

    public static void Initialize(IntPtr hwndMain, IntPtr hwndOverlay, AppConfig config)
    {
        _hwndMain = hwndMain;
        _hwndOverlay = hwndOverlay;

        // TOPMOST 재적용 타이머
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_TOPMOST,
            (uint)config.Advanced.ForceTopmostIntervalMs, IntPtr.Zero);
    }

    public static void Dispose()
    {
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_FADE);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HOLD);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HIGHLIGHT);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_SLIDE);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_TOPMOST);
    }

    // ================================================================
    // TriggerShow
    // ================================================================

    public static void TriggerShow(int x, int y,
        ImeState state, AppConfig config, bool imeChanged)
    {
        // 0. NonKoreanIme "hide" 가드
        if (state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Hide)
        {
            TriggerHide(config, forceHidden: true);
            return;
        }

        // 1. hold 타이머 duration
        _holdDurationMs = config.DisplayMode == DisplayMode.Always
            ? config.AlwaysIdleTimeoutMs
            : config.EventDisplayDurationMs;

        // 2. targetAlpha 계산
        _currentState = state;
        _targetAlpha = GetTargetAlpha(config, active: true);

        // 3. 상태별 분기
        if (_phase == AnimPhase.Holding || _phase == AnimPhase.FadingIn)
        {
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HOLD);
            User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_HOLD,
                (uint)_holdDurationMs, IntPtr.Zero);

            var (prevX, prevY) = Overlay.GetLastPosition();
            Overlay.Show(x, y, state, config);
            TryStartSlide(prevX, prevY, config);

            if (imeChanged)
            {
                Overlay.UpdateColor(state, config);
                if (config.ChangeHighlight)
                    StartHighlight(config);
            }

            if (_currentAlpha != _targetAlpha)
            {
                _currentAlpha = _targetAlpha;
                Overlay.UpdateAlpha(_targetAlpha);
            }
        }
        else if (_phase == AnimPhase.Idle)
        {
            StartFade(_currentAlpha, _targetAlpha, config.FadeInMs);
            _phase = AnimPhase.FadingIn;
            User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_FADE, DefaultConfig.AnimationFrameMs, IntPtr.Zero);

            var (prevX, prevY) = Overlay.GetLastPosition();
            Overlay.Show(x, y, state, config);
            TryStartSlide(prevX, prevY, config);

            if (imeChanged)
            {
                Overlay.UpdateColor(state, config);
                if (config.ChangeHighlight)
                    StartHighlight(config);
            }

            if (_currentAlpha != _targetAlpha)
            {
                _currentAlpha = _targetAlpha;
                Overlay.UpdateAlpha(_targetAlpha);
            }
        }
        else if (_phase == AnimPhase.FadingOut)
        {
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_FADE);
            StartFade(_currentAlpha, _targetAlpha, config.FadeInMs);
            _phase = AnimPhase.FadingIn;
            User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_FADE, DefaultConfig.AnimationFrameMs, IntPtr.Zero);

            var (prevX, prevY) = Overlay.GetLastPosition();
            Overlay.Show(x, y, state, config);
            TryStartSlide(prevX, prevY, config);

            if (imeChanged)
            {
                Overlay.UpdateColor(state, config);
                if (config.ChangeHighlight)
                    StartHighlight(config);
            }
        }
        else // Hidden
        {
            Overlay.Show(x, y, state, config);
            User32.ShowWindow(_hwndOverlay, Win32Constants.SW_SHOW);

            if (config.AnimationEnabled)
            {
                StartFade(0, _targetAlpha, config.FadeInMs);
                _phase = AnimPhase.FadingIn;
                User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_FADE, DefaultConfig.AnimationFrameMs, IntPtr.Zero);
            }
            else
            {
                _currentAlpha = _targetAlpha;
                Overlay.UpdateAlpha(_targetAlpha);
                _phase = AnimPhase.Holding;
                User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_HOLD,
                    (uint)_holdDurationMs, IntPtr.Zero);
            }

            if (imeChanged && config.ChangeHighlight)
                StartHighlight(config);
        }
    }

    // ================================================================
    // HandleTimer
    // ================================================================

    public static void HandleTimer(nuint timerId, AppConfig config)
    {
        if (timerId == AppMessages.TIMER_ID_FADE)
            HandleFadeTimer(config);
        else if (timerId == AppMessages.TIMER_ID_HOLD)
            HandleHoldTimer(config);
        else if (timerId == AppMessages.TIMER_ID_HIGHLIGHT)
            HandleHighlightTimer(config);
        else if (timerId == AppMessages.TIMER_ID_SLIDE)
            HandleSlideTimer();
        else if (timerId == AppMessages.TIMER_ID_TOPMOST)
            Overlay.ForceTopmost();
    }

    private static void HandleFadeTimer(AppConfig config)
    {
        double elapsed = GetElapsedMs(_fadeStartTick);
        double ratio = _fadeDurationMs > 0 ? Math.Clamp(elapsed / _fadeDurationMs, 0.0, 1.0) : 1.0;

        byte alpha = (byte)(_fadeStartAlpha + (_fadeEndAlpha - _fadeStartAlpha) * ratio);
        _currentAlpha = alpha;
        Overlay.UpdateAlpha(alpha);

        if (ratio >= 1.0)
        {
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_FADE);

            if (_phase == AnimPhase.FadingIn)
            {
                _currentAlpha = _fadeEndAlpha;
                _phase = AnimPhase.Holding;
                User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_HOLD,
                    (uint)_holdDurationMs, IntPtr.Zero);
            }
            else if (_phase == AnimPhase.FadingOut)
            {
                if (_forceHidden || config.DisplayMode != DisplayMode.Always)
                {
                    // OnEvent 모드 또는 forceHidden → 완전 숨김
                    Overlay.Hide();
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

    private static void HandleHoldTimer(AppConfig config)
    {
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HOLD);

        if (config.DisplayMode == DisplayMode.Always)
        {
            // Always 모드: 페이드아웃 없이 현재 alpha 유지 → Idle
            _phase = AnimPhase.Idle;
            return;
        }

        // OnEvent 모드: 페이드아웃 → Hidden
        StartFade(_currentAlpha, 0, config.FadeOutMs);
        _phase = AnimPhase.FadingOut;
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_FADE, DefaultConfig.AnimationFrameMs, IntPtr.Zero);
    }

    private static void HandleHighlightTimer(AppConfig config)
    {
        if (!_highlightActive) return;

        double elapsed = GetElapsedMs(_highlightStartTick);
        double ratio = config.HighlightDurationMs > 0
            ? Math.Clamp(elapsed / config.HighlightDurationMs, 0.0, 1.0) : 1.0;

        double scale = _highlightStartScale + (1.0 - _highlightStartScale) * ratio;

        var (baseW, baseH) = Overlay.GetBaseSize();
        var (lastX, lastY) = Overlay.GetLastPosition();

        int newW = (int)Math.Round(baseW * scale);
        int newH = (int)Math.Round(baseH * scale);
        int newX = lastX - (newW - baseW) / 2;
        int newY = lastY - (newH - baseH) / 2;

        Overlay.UpdateScaledSize(newX, newY, newW, newH, _currentAlpha);

        if (ratio >= 1.0)
        {
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HIGHLIGHT);
            _highlightActive = false;

            // 원래 크기 복원
            Overlay.UpdateScaledSize(lastX, lastY, baseW, baseH, _currentAlpha);
        }
    }

    // ================================================================
    // TriggerHide
    // ================================================================

    public static void TriggerHide(AppConfig config, bool forceHidden = false)
    {
        // 이미 숨김 상태면 무시
        if (_phase == AnimPhase.Hidden) return;
        if (_phase == AnimPhase.Idle && !forceHidden) return;

        // 1. 모든 타이머 정리
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_FADE);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HOLD);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_HIGHLIGHT);
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_SLIDE);
        _highlightActive = false;
        _slideActive = false;

        // 2. 모드별 분기
        if (config.DisplayMode == DisplayMode.Always && !forceHidden)
        {
            // Always 모드: 페이드아웃 없이 현재 alpha 유지 → Idle
            _phase = AnimPhase.Idle;
        }
        else
        {
            // OnEvent 모드 또는 forceHidden
            if (config.AnimationEnabled && _currentAlpha > 0)
            {
                _forceHidden = forceHidden;
                StartFade(_currentAlpha, 0, config.FadeOutMs);
                _phase = AnimPhase.FadingOut;
                User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_FADE, DefaultConfig.AnimationFrameMs, IntPtr.Zero);
            }
            else
            {
                Overlay.Hide();
                _phase = AnimPhase.Hidden;
                _currentAlpha = 0;
            }
        }
    }

    // ================================================================
    // 헬퍼
    // ================================================================

    private static byte GetTargetAlpha(AppConfig config, bool active)
    {
        double raw;

        if (config.DisplayMode == DisplayMode.Always)
            raw = active ? config.ActiveOpacity : config.IdleOpacity;
        else
            raw = config.Opacity;

        // Dim 모드: NonKorean + Dim이면 50% 감소
        if (_currentState == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Dim)
            raw *= DefaultConfig.DimOpacityFactor;

        return (byte)(raw * 255);
    }

    private static void StartFade(byte from, byte to, int durationMs)
    {
        _fadeStartAlpha = from;
        _fadeEndAlpha = to;
        _fadeDurationMs = durationMs;
        _fadeStartTick = Stopwatch.GetTimestamp();
    }

    private static void StartHighlight(AppConfig config)
    {
        _highlightActive = true;
        _highlightStartScale = config.HighlightScale;
        _highlightStartTick = Stopwatch.GetTimestamp();
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_HIGHLIGHT, DefaultConfig.AnimationFrameMs, IntPtr.Zero);
    }

    private static double GetElapsedMs(long startTick)
    {
        long now = Stopwatch.GetTimestamp();
        return (now - startTick) * 1000.0 / Stopwatch.Frequency;
    }

    // ================================================================
    // 슬라이드 애니메이션
    // ================================================================

    /// <summary>
    /// Overlay.Show 호출 후 이전/새 위치를 비교하여 슬라이드 시작.
    /// DWM VSync 내 같은 메시지 핸들러에서 Show→UpdatePosition 연속 호출이므로
    /// 중간 위치는 화면에 표시되지 않는다.
    /// </summary>
    private static void TryStartSlide(int prevX, int prevY, AppConfig config)
    {
        var (newX, newY) = Overlay.GetLastPosition();

        if (config.SlideAnimation && config.SlideSpeedMs > 0
            && (prevX != newX || prevY != newY))
        {
            // 이전 위치로 즉시 복원 (같은 프레임 — DWM이 수집 전)
            Overlay.UpdatePosition(prevX, prevY);
            StartSlide(prevX, prevY, newX, newY, config.SlideSpeedMs);
        }
    }

    private static void StartSlide(int fromX, int fromY, int toX, int toY, int durationMs)
    {
        _slideActive = true;
        _slideFromX = fromX;
        _slideFromY = fromY;
        _slideToX = toX;
        _slideToY = toY;
        _slideDurationMs = durationMs;
        _slideStartTick = Stopwatch.GetTimestamp();
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_SLIDE, DefaultConfig.AnimationFrameMs, IntPtr.Zero);
    }

    private static void HandleSlideTimer()
    {
        if (!_slideActive) return;

        double elapsed = GetElapsedMs(_slideStartTick);
        double t = _slideDurationMs > 0 ? Math.Clamp(elapsed / _slideDurationMs, 0.0, 1.0) : 1.0;

        // ease-out cubic: 1 - (1-t)^3
        double eased = 1.0 - (1.0 - t) * (1.0 - t) * (1.0 - t);

        int x = _slideFromX + (int)Math.Round((_slideToX - _slideFromX) * eased);
        int y = _slideFromY + (int)Math.Round((_slideToY - _slideFromY) * eased);

        Overlay.UpdatePosition(x, y);

        if (t >= 1.0)
        {
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_SLIDE);
            _slideActive = false;
            // 최종 위치 보정
            Overlay.UpdatePosition(_slideToX, _slideToY);
        }
    }
}
