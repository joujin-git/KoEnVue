using KoEnVue.App.Config;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
using KoEnVue.Core.Color;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

// Win32Constants / POINT 등은 KoEnVue.Core.Native — using 위.

namespace KoEnVue.App.UI;

/// <summary>
/// 커서 추종 인디케이터의 정적 파사드. <see cref="LayeredCursorBase"/> 엔진 + 마우스 정지 검출 +
/// IME/CAPS 상태 → <see cref="CursorStyle"/> 합성 책임.
/// <para>
/// 사용자 모델 — 동심원 3개 (Inner / Middle / Outer) + 헤일로. CAPS OFF 시 Outer 원 미표시.
/// </para>
/// <list type="bullet">
///   <item><b>정지 시 표시 모드 (디폴트)</b>: 마우스 이동 → 즉시 숨김. 정지 후 <c>idle_delay_ms</c> 경과 → 표시.</item>
///   <item><b>항상 표시 모드</b> (<see cref="AppConfig.CursorAlwaysShow"/> = true): 폴링으로 위치 추종, 숨김 안 함. 이동 중 시인성 저하(PR-29) 가능.</item>
/// </list>
/// <para>
/// 색상 정책 — Inner/Middle 은 현재 IME 색상 (Hangul/English/NonKorean 각자), Outer 는 영문일 때
/// 한글 색상, 한글/비한글일 때 영문 색상 (한글/비한글을 같은 카테고리로 통합 — 사용자 인터뷰 결정).
/// </para>
/// <para>
/// 모든 메서드는 메인 UI 스레드에서만 호출된다. P6 게이트: 본 파사드는 App 측이므로 ImeState / AppConfig
/// 어휘 사용 OK — 엔진 (<see cref="LayeredCursorBase"/>) 은 <see cref="CursorStyle"/> 만 받아 P6 보존.
/// </para>
/// </summary>
internal static class CursorOverlay
{
    // ================================================================
    // 상태
    // ================================================================

    private static bool _initialized;
    private static LayeredCursorBase? _engine;
    private static AppConfig _config = new();
    private static ImeState _lastImeState = ImeState.Hangul;
    private static bool _capsLockOn;
    private static CursorStyle _currentStyle;

    private static bool _isVisible;
    private static int _lastCursorX;
    private static int _lastCursorY;
    // 정지 진입 tick (Environment.TickCount64 ms). 0 = 아직 정지 진입 안 함 / 이동 중.
    private static long _idleStartTick;
    // topmost 직전 재적용 tick (Environment.TickCount64 ms). 0 = 아직 재적용 안 함.
    // 항상 표시 + 정지 검출(가시) 모드 양쪽의 주기 재적용 게이트 기준점.
    private static long _lastTopmostTick;

    // IME 전환 스케일 팝 (플로팅 배지 ChangeHighlight 와 동형). 별도 엔진(P4 예외)이라 OverlayAnimator 미재사용 —
    // 경량 상태(_popActive + tick)로 구현. _hwndTimer 에 TIMER_ID_CURSOR_POP(16ms) 등록.
    private static IntPtr _hwndTimer;     // 팝 WM_TIMER 등록 대상 (= 메인 윈도우). Initialize 에서 주입.
    private static bool _popActive;       // 팝 진행 중 여부.
    private static long _popStartTick;    // 팝 시작 tick (Environment.TickCount64 ms).

    // PR-29 이동 중 시인성 저하 — AlwaysShow 경로에서만 사용.
    private static bool _motionDimActive;
    private static int _motionStillPolls;

    // 셸 UI 호버 판정 캐시 — 직전 폴링 tick 의 루트 hwnd 와 그 판정 결과. 같은 창 위에 머무는 동안
    // GetProcessName(OpenProcess) 반복 호출을 피한다 (마우스가 창 경계를 넘을 때만 재평가).
    private static IntPtr _lastShellHwnd;
    private static bool _lastShellResult;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 엔진 초기화. <c>hwnd</c> 는 <c>Program.Bootstrap.CreateCursorOverlayWindow</c> 가 생성한 별도 HWND.
    /// <c>hwndTimer</c> 는 메인 윈도우 — IME 전환 스케일 팝 타이머(<c>TIMER_ID_CURSOR_POP</c>)를 건다.
    /// 첫 호출 시 DIB 사전 생성 (PrepareResources) — 다음 Show 호출에서 가시화.
    /// </summary>
    public static void Initialize(IntPtr hwnd, IntPtr hwndTimer, AppConfig config, ImeState initialState, bool initialCapsLockOn)
    {
        _config = config;
        _hwndTimer = hwndTimer;
        _lastImeState = initialState;
        _capsLockOn = initialCapsLockOn;
        _engine = new LayeredCursorBase(hwnd, CursorRenderer.Render);
        _currentStyle = BuildStyle(config, initialState, initialCapsLockOn);
        _engine.PrepareResources(_currentStyle);
        _isVisible = false;
        _idleStartTick = 0;
        _lastTopmostTick = 0;
        _popActive = false;
        _popStartTick = 0;
        _motionDimActive = false;
        _motionStillPolls = 0;
        _lastShellHwnd = IntPtr.Zero;
        _lastShellResult = false;
        // 첫 틱 이동 판정이 원점(0,0) 기준이 되지 않도록 현재 커서 좌표로 시드 (PR-28 A안 #3).
        if (User32.GetCursorPos(out POINT seed))
        {
            _lastCursorX = seed.X;
            _lastCursorY = seed.Y;
        }
        _initialized = true;
        Logger.Info("CursorOverlay initialized");
    }

    /// <summary>
    /// config 변경 통지. cursor 키 (radius / thickness / halo / 색상) 어느 것이 바뀌어도 본 메서드 한 번으로
    /// 흡수 — DPI 캐시 무효화 + 스타일 재합성 + DIB 재생성 + 가시 중이면 즉시 재렌더.
    /// </summary>
    public static void HandleConfigChanged(AppConfig config)
    {
        _config = config;
        // 셸 UI 판정 캐시 무효화 — SystemHideClasses/Processes 가 바뀌면 같은 루트 창 위에 머물러도
        // 새 규칙으로 재평가되도록 (리셋 없으면 마우스가 창 경계를 넘을 때까지 stale 판정 유지).
        // _lastShellHwnd=Zero 만으로 충분 — 다음 IsOverShellUi 의 root(유효 hwnd) != Zero 라 재평가됨.
        _lastShellHwnd = IntPtr.Zero;
        if (_engine is null) return;

        StopPop();  // config 변경 시 진행 중 팝 중단 — 스타일 재합성 전 정리.
        ResetMotionDim();
        _engine.HandleDpiChanged();
        _currentStyle = BuildStyle(config, _lastImeState, _capsLockOn);
        // 가시 중이면 Prepare(alpha=0) + Render 이중 블리트 대신 Render 1회 (PR-28 A안 #2).
        if (_isVisible)
        {
            _engine.Render(_currentStyle);
            _engine.UpdateAlpha(CursorMotionDim.FullAlpha);
        }
        else
        {
            _engine.PrepareResources(_currentStyle);
        }
        Logger.Debug($"Cursor halo config applied (alwaysShow={config.CursorAlwaysShow}, displayMode={config.CursorDisplayMode}, visible={_isVisible})");
    }

    /// <summary>
    /// 폴링 진입점. WM_TIMER 핸들러에서 호출.
    /// 마우스 좌표 + 이전 위치 거리 → motion threshold 넘으면 "이동", 미만이면 "정지".
    /// </summary>
    public static void HandleCursorMotionTimer()
    {
        if (!_initialized || _engine is null) return;
        if (!User32.GetCursorPos(out POINT cursor)) return;

        int dx = Math.Abs(cursor.X - _lastCursorX);
        int dy = Math.Abs(cursor.Y - _lastCursorY);
        int manhattan = dx + dy;
        // AlwaysShow 딤(PR-30): 저임계 — 저속 이동도 딤. 숨김 모드: 기존 Threshold 유지(과민 방지).
        bool movingForDim = manhattan > DefaultConfig.CursorMotionDimThresholdPx;
        bool movingForHide = manhattan > _config.CursorMotionThresholdPx;

        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        // 셸·메뉴 (작업 표시줄 / 시작 / 검색 / #32768 등) 위에서는 커서 헤일로를 숨긴다. 셸은 시스템
        // z-band (시작/검색은 immersive 밴드) 라 일반 topmost 인 커서 헤일로가 위로 못 올라가 가려지므로,
        // 가려진 채 어색하게 두지 않고 해당 영역에서는 일관되게 숨긴다 (사용자 결정 2026-06-01 + PR-32).
        if (IsOverShellUi(cursor))
        {
            HideCursor("Cursor halo hidden (over shell UI)");
            _idleStartTick = 0;
            ResetMotionDim();
            return;
        }

        if (_config.CursorAlwaysShow)
        {
            ApplyMotionDimAndRender(cursor, movingForDim);
            MaybeReassertTopmost();
            return;
        }

        if (movingForHide)
        {
            _idleStartTick = 0;
            HideCursor("Cursor halo hidden (cursor moving)");
            return;
        }

        // 정지 상태
        if (_isVisible)
        {
            // 가시 상태로 정지 중 — 다른 topmost 창에 가려져도 복구하도록 주기 재적용.
            MaybeReassertTopmost();
            return; // 이미 가시 — 정지 시점에 잡혀 있음
        }

        long now = Environment.TickCount64;
        if (_idleStartTick == 0)
        {
            _idleStartTick = now;
            return;
        }

        if (now - _idleStartTick >= _config.CursorIdleDelayMs)
        {
            RenderAtCursor(cursor);
            _idleStartTick = 0;
        }
    }

    /// <summary>
    /// IME 상태 변경 통지. 색상 캐시 갱신 + 가시 중이면 즉시 재렌더 (DIB bbox 불변 → 픽셀만 재계산).
    /// </summary>
    public static void SetImeState(ImeState state)
    {
        if (_lastImeState == state) return;
        _lastImeState = state;
        _currentStyle = RebuildStylePreservingMotion(state, _capsLockOn);
        Logger.Debug($"Cursor halo IME state: {state} (visible={_isVisible})");
        if (_isVisible && _engine is not null)
        {
            // 가시 상태에서 IME 가 실제로 바뀜 — 마스터(AnimationEnabled) + CursorChangeHighlight 면 스케일 팝
            // (첫 프레임이 새 색 + 시작 배율로 렌더하므로 별도 색 갱신 Render 불요), 아니면 색만 즉시 갱신.
            // 플로팅 배지 Animation.BuildAnimationConfig 의 `AnimationEnabled && ChangeHighlight` 마스터 게이팅과 동형 (PR-22 후속).
            if (_config.AnimationEnabled && _config.CursorChangeHighlight)
                TriggerPop();
            else
                _engine.Render(_currentStyle);
        }
    }

    /// <summary>
    /// CAPS LOCK 토글 통지. Outer 원 표시 여부 + 색상 갱신.
    /// </summary>
    public static void SetCapsLock(bool on)
    {
        if (_capsLockOn == on) return;
        _capsLockOn = on;
        _currentStyle = RebuildStylePreservingMotion(_lastImeState, on);
        Logger.Debug($"Cursor halo CapsLock: {(on ? "ON" : "OFF")} (visible={_isVisible})");
        if (_isVisible && _engine is not null)
            _engine.Render(_currentStyle);
    }

    public static void Dispose()
    {
        bool wasInitialized = _initialized;
        StopPop();
        _engine?.Dispose();
        _engine = null;
        _initialized = false;
        _isVisible = false;
        _idleStartTick = 0;
        _lastTopmostTick = 0;
        _popStartTick = 0;
        ResetMotionDim();
        if (wasInitialized)
            Logger.Info("CursorOverlay disposed");
    }

    // ================================================================
    // 내부
    // ================================================================

    /// <summary>가시 상태면 엔진 숨김 + 가시 플래그 해제 + 진행 중 팝 정리 + 사유 로깅. 셸 UI 진입 / 이동
    /// 시작 양쪽이 공유한다 (멱등 — 이미 숨김이면 no-op).</summary>
    private static void HideCursor(string reason)
    {
        if (!_isVisible || _engine is null) return;
        _engine.Hide();
        _isVisible = false;
        StopPop();
        ResetMotionDim();
        Logger.Debug(reason);
    }

    /// <summary>
    /// AlwaysShow 경로 — 커서 표시 모드(PR-31)에 따라 안개/선명 + 위치 렌더.
    /// Soft=항상 흐릿하게, Sharp=항상 선명하게, Motion=이동 중 흐릿하게(PR-29/30).
    /// 팝 중에는 Soft/딤 안개 유지(HighlightScale만 갱신 — PR-31 후속). 창 SourceConstantAlpha 는 Full.
    /// </summary>
    private static void ApplyMotionDimAndRender(POINT cursor, bool moving)
    {
        if (_engine is null) return;

        CursorDisplayMode mode = _config.CursorDisplayMode;
        _motionDimActive = CursorMotionDim.AdvanceForMode(
            mode, ref _motionStillPolls, moving, _motionDimActive, DefaultConfig.CursorMotionDimSettlePolls);

        double soft = CursorMotionDim.EffectiveSoftness(
            _motionDimActive, _config.CursorMotionSoftness);
        var rings = CursorMotionDim.RingAlphas(
            _motionDimActive, _config.CursorMotionAlpha,
            DefaultConfig.CursorMotionRingInnerFactor,
            DefaultConfig.CursorMotionRingMiddleFactor,
            DefaultConfig.CursorMotionRingOuterFactor,
            DefaultConfig.MinCursorMotionAlpha);

        _currentStyle = _currentStyle with
        {
            MotionSoftness = soft,
            MotionFogPadLogicalPx = soft > 0.0 ? DefaultConfig.CursorMotionFogPadLogicalPx : 0,
            RingAlphaInner = rings.Inner,
            RingAlphaMiddle = rings.Middle,
            RingAlphaOuter = rings.Outer,
        };

        _engine.SetDisplayAlpha(CursorMotionDim.FullAlpha);
        RenderAtCursor(cursor);
    }

    private static void ResetMotionDim()
    {
        _motionDimActive = false;
        _motionStillPolls = 0;
    }

    /// <summary>
    /// cursor 위치 = DIB 정중앙. ShowAtCenter 가 monitor DPI + bbox 직접 계산해 정확한 좌상단 좌표 set.
    /// Render 가 같은 DPI 로 DIB 생성 → race 없음. 윈도우는 WS_VISIBLE 영구 박혀있어 ShowWindow 호출
    /// 불요 — Render 의 UpdateLayeredWindow 가 Full 알파로 표시 (딤은 셰이더 원별 알파).
    /// <para>
    /// 첫 가시화 (<c>!_isVisible</c>) 시 <see cref="ApplyTopmost"/> 로 명시 topmost 진입 +
    /// <c>_lastTopmostTick</c> 주기 카운터 첫 기준점 set. 이후 주기 재적용은
    /// <see cref="MaybeReassertTopmost"/> (HandleCursorMotionTimer 양 모드 분기).
    /// </para>
    /// </summary>
    private static void RenderAtCursor(POINT cursor)
    {
        if (_engine is null) return;

        _engine.ShowAtCenter(cursor.X, cursor.Y, _currentStyle);
        _engine.Render(_currentStyle);

        if (!_isVisible)
        {
            ApplyTopmost();
            _lastTopmostTick = Environment.TickCount64;  // 주기 재적용 카운터 첫 기준점
            Logger.Debug($"Cursor halo shown at ({cursor.X},{cursor.Y}); topmost set");
        }

        _isVisible = true;
    }

    /// <summary>
    /// 커서 윈도우를 <see cref="Win32Constants.HWND_TOPMOST"/> 로 (재)설정. 첫 표시
    /// (<see cref="RenderAtCursor"/>) + 주기 재적용 (<see cref="MaybeReassertTopmost"/>) 단일 코드 경로.
    /// <para>
    /// cursor 윈도우는 생성 시 <c>WS_EX_TOPMOST</c> 없이 일반 z-order 로 시작 (Program.Bootstrap —
    /// cursor 첫 UpdateLayeredWindow 가 DWM 합성에서 다른 topmost (Shell_TrayWnd) 재정렬 → foreground
    /// 잠시 변경 → 플로팅 배지 SystemFilter hide 회귀 방지). <c>SWP_NOSENDCHANGING</c> 으로 다른 윈도우에
    /// <c>WM_WINDOWPOSCHANGING</c> 알림 차단 — Shell_TrayWnd 등 z-order 재정렬 trigger 없음.
    /// </para>
    /// </summary>
    private static void ApplyTopmost()
    {
        if (_engine is null) return;
        User32.SetWindowPos(_engine.Hwnd, Win32Constants.HWND_TOPMOST,
            0, 0, 0, 0,
            Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE
            | Win32Constants.SWP_NOACTIVATE | Win32Constants.SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// topmost 주기 재적용 게이트 — 직전 재적용 후 <see cref="DefaultConfig.CursorForceTopmostIntervalMs"/>
    /// 경과 시에만 <see cref="ApplyTopmost"/> 재호출. 항상 표시 모드 + 정지 검출 모드(가시 상태) 양쪽에서
    /// 호출 — 다른 topmost 창(풀스크린/토스트/UAC)이 위로 올라와도 복구. interval ≤ 0 이면 비활성
    /// (첫 표시 set 만 유지 = fix 전 동작). 매 tick 아님 — 기본 5초 경과 시에만
    /// (dev-notes "매 프레임 topmost 호출 지양" 준수 + SWP_NOSENDCHANGING 으로 가설 CC 회귀 차단).
    /// </summary>
    private static void MaybeReassertTopmost()
    {
        int interval = DefaultConfig.CursorForceTopmostIntervalMs;
        if (interval <= 0) return;
        long now = Environment.TickCount64;
        if (now - _lastTopmostTick < interval) return;
        ApplyTopmost();
        _lastTopmostTick = now;
        Logger.Debug($"Cursor halo topmost reasserted (interval={interval}ms)");
    }

    /// <summary>
    /// IME 전환 스케일 팝 시작 — 플로팅 배지 <c>OverlayAnimator</c> Highlight 와 동형. 별도 엔진(P4 예외)
    /// 이라 OverlayAnimator 를 재사용하지 않고 경량 상태(<see cref="_popActive"/> + tick)로 구현한다.
    /// 첫 프레임을 즉시 렌더해 16ms 지연 없이 시작 배율(<see cref="AppConfig.CursorHighlightScale"/>)로 팝.
    /// </summary>
    private static void TriggerPop()
    {
        if (_engine is null || _hwndTimer == IntPtr.Zero) return;
        _popActive = true;
        _popStartTick = Environment.TickCount64;
        User32.SetTimer(_hwndTimer, AppMessages.TIMER_ID_CURSOR_POP, DefaultConfig.AnimationFrameMs, IntPtr.Zero);
        Logger.Debug($"Cursor halo pop started (scale={_config.CursorHighlightScale}, durationMs={_config.CursorHighlightDurationMs})");
        HandleCursorPopTimer();  // 즉시 첫 프레임(시작 배율) 렌더 — 16ms 대기 없이 팝 개시.
    }

    /// <summary>
    /// 팝 프레임 (16ms WM_TIMER). 시작 배율 → 1.0 선형 보간(메인 Highlight 와 동일 식)을
    /// <see cref="CursorStyle.HighlightScale"/> 에 반영해 재렌더. bbox 는 <see cref="CursorStyle.MaxHighlightScale"/>
    /// 기준 고정이라 DIB 재생성 없이 셰이더만 재계산. ratio 1.0 도달 시 타이머 정리 + 1.0 복원.
    /// </summary>
    public static void HandleCursorPopTimer()
    {
        if (!_popActive || _engine is null) return;
        long elapsed = Environment.TickCount64 - _popStartTick;
        int durationMs = _config.CursorHighlightDurationMs;
        double ratio = durationMs > 0 ? Math.Clamp((double)elapsed / durationMs, 0.0, 1.0) : 1.0;
        double scale = _config.CursorHighlightScale + (1.0 - _config.CursorHighlightScale) * ratio;
        // HighlightScale 만 갱신 — Soft/딤 안개(MotionSoftness·RingAlpha*) 유지.
        // 예전 ClearMotionDimStyle 은 한/영 팝마다 선명 플래시를 일으킴 (PR-31 후속).
        _currentStyle = _currentStyle with { HighlightScale = scale };
        _engine.Render(_currentStyle);
        _engine.UpdateAlpha(CursorMotionDim.FullAlpha);
        if (ratio >= 1.0)
            StopPop();
    }

    /// <summary>
    /// 진행 중 팝 중단 — 타이머 정리 + <see cref="CursorStyle.HighlightScale"/> 1.0 복원. 팝 자연 완료
    /// (ratio 1.0) + 숨김/이동/config 변경/Dispose 에서 호출. <see cref="_popActive"/> 가드로 멱등.
    /// </summary>
    private static void StopPop()
    {
        if (!_popActive) return;
        _popActive = false;
        if (_hwndTimer != IntPtr.Zero)
            User32.KillTimer(_hwndTimer, AppMessages.TIMER_ID_CURSOR_POP);
        _currentStyle = _currentStyle with { HighlightScale = 1.0 };
    }

    /// <summary>
    /// AppConfig + IME 상태 + CAPS 토글 → CursorStyle 합성. 한글/비한글 IME 는 같은 카테고리로 묶어
    /// CAPS Outer 색상이 영문 색상이 되고, 영문 IME 일 때만 CAPS 가 한글 색상 (인터뷰 결정).
    /// </summary>
    private static CursorStyle RebuildStylePreservingMotion(ImeState state, bool capsOn)
    {
        double soft = _currentStyle.MotionSoftness;
        int fogPad = _currentStyle.MotionFogPadLogicalPx;
        double ri = _currentStyle.RingAlphaInner;
        double rm = _currentStyle.RingAlphaMiddle;
        double ro = _currentStyle.RingAlphaOuter;
        double hs = _currentStyle.HighlightScale;
        return BuildStyle(_config, state, capsOn) with
        {
            MotionSoftness = soft,
            MotionFogPadLogicalPx = fogPad,
            RingAlphaInner = ri,
            RingAlphaMiddle = rm,
            RingAlphaOuter = ro,
            HighlightScale = hs,
        };
    }

    private static CursorStyle BuildStyle(AppConfig config, ImeState state, bool capsOn)
    {
        string currentBg = state switch
        {
            ImeState.Hangul => config.HangulBg,
            ImeState.English => config.EnglishBg,
            _ => config.NonKoreanBg
        };
        string capsBg = state == ImeState.English ? config.HangulBg : config.EnglishBg;

        uint innerArgb = ColorHelper.HexToArgb(currentBg);
        uint outerArgb = ColorHelper.HexToArgb(capsBg);

        return new CursorStyle(
            OuterRadiusLogicalPx: config.CursorOuterRadius,
            MiddleRadiusLogicalPx: config.CursorMiddleRadius,
            InnerRadiusLogicalPx: config.CursorInnerRadius,
            CoreThicknessLogicalPx: config.CursorCoreThickness,
            HaloThicknessLogicalPx: config.CursorHaloThickness,
            HaloOpacity: config.CursorHaloOpacity,
            InnerColorArgb: innerArgb,
            MiddleColorArgb: innerArgb,
            OuterColorArgb: outerArgb,
            CapsLockOn: capsOn
        );
    }

    /// <summary>
    /// 커서 바로 아래 창이 셸·메뉴 suppress 표면인지 (PR-32).
    /// <see cref="User32.WindowFromPoint"/> 는 WS_EX_TRANSPARENT 커서 헤일로를 통과한다.
    /// <see cref="Win32Constants.GA_ROOT"/> 루트 캐시로 매 tick GetProcessName 을 피한다.
    /// 판정은 <see cref="OverlaySuppressProbe"/> 단일 진실원 (메인 Pointer 축과 공유, Start/Search 포함).
    /// </summary>
    private static bool IsOverShellUi(POINT cursor)
    {
        IntPtr hwnd = User32.WindowFromPoint(cursor);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr root = User32.GetAncestor(hwnd, Win32Constants.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;

        if (root == _lastShellHwnd) return _lastShellResult;

        bool result = OverlaySuppressProbe.IsSuppressRoot(
            root, _config, includeSystemInputProcesses: true);
        _lastShellHwnd = root;
        _lastShellResult = result;
        return result;
    }
}
