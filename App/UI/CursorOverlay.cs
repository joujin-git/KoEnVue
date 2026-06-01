using KoEnVue.App.Config;
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
///   <item><b>항상 표시 모드</b> (<see cref="AppConfig.CursorAlwaysShow"/> = true): 16ms 폴링으로 위치 추종, 숨김 안 함.</item>
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

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 엔진 초기화. <c>hwnd</c> 는 <c>Program.Bootstrap.CreateCursorOverlayWindow</c> 가 생성한 별도 HWND.
    /// 첫 호출 시 DIB 사전 생성 (PrepareResources) — 다음 Show 호출에서 가시화.
    /// </summary>
    public static void Initialize(IntPtr hwnd, AppConfig config, ImeState initialState, bool initialCapsLockOn)
    {
        _config = config;
        _lastImeState = initialState;
        _capsLockOn = initialCapsLockOn;
        _engine = new LayeredCursorBase(hwnd, CursorRenderer.Render);
        _currentStyle = BuildStyle(config, initialState, initialCapsLockOn);
        _engine.PrepareResources(_currentStyle);
        _isVisible = false;
        _idleStartTick = 0;
        _lastTopmostTick = 0;
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
        if (_engine is null) return;

        _engine.HandleDpiChanged();
        _currentStyle = BuildStyle(config, _lastImeState, _capsLockOn);
        _engine.PrepareResources(_currentStyle);
        if (_isVisible)
        {
            _engine.Render(_currentStyle);
            _engine.UpdateAlpha(255);
        }
    }

    /// <summary>
    /// 50ms (정지 검출 모드) / 16ms (항상 표시 모드) 폴링 진입점. WM_TIMER 핸들러에서 호출.
    /// 마우스 좌표 + 이전 위치 거리 → motion threshold 넘으면 "이동", 미만이면 "정지". 정지 진입
    /// idle_delay_ms 경과 시 표시.
    /// </summary>
    public static void HandleCursorMotionTimer()
    {
        if (!_initialized || _engine is null) return;
        if (!User32.GetCursorPos(out POINT cursor)) return;

        // 시스템 창 (작업 표시줄 / 시작 버튼 / 검색 박스 / 트레이 아이콘 등) 위에서도 cursor 인디
        // 일관 표시. 사용자 결정: "작업 표시줄에 가려지겠지만 일관적이면 괜찮음". 이전 SystemHideClasses
        // 체크 분기 제거.

        int dx = Math.Abs(cursor.X - _lastCursorX);
        int dy = Math.Abs(cursor.Y - _lastCursorY);
        bool moving = (dx + dy) > _config.CursorMotionThresholdPx;

        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        if (_config.CursorAlwaysShow)
        {
            // 항상 표시 모드 — 가시화 보장 + 매 tick 위치 추종. idle 검출/숨김 skip.
            RenderAtCursor(cursor);
            // 다른 topmost 창(풀스크린/토스트/UAC)이 위로 올라와도 복구 — 주기 재적용.
            MaybeReassertTopmost();
            return;
        }

        if (moving)
        {
            _idleStartTick = 0;
            if (_isVisible)
            {
                _engine.Hide();
                _isVisible = false;
            }
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
        _currentStyle = BuildStyle(_config, state, _capsLockOn);
        if (_isVisible && _engine is not null)
            _engine.Render(_currentStyle);
    }

    /// <summary>
    /// CAPS LOCK 토글 통지. Outer 원 표시 여부 + 색상 갱신.
    /// </summary>
    public static void SetCapsLock(bool on)
    {
        if (_capsLockOn == on) return;
        _capsLockOn = on;
        _currentStyle = BuildStyle(_config, _lastImeState, on);
        if (_isVisible && _engine is not null)
            _engine.Render(_currentStyle);
    }

    public static void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        _initialized = false;
        _isVisible = false;
        _idleStartTick = 0;
        _lastTopmostTick = 0;
    }

    // ================================================================
    // 내부
    // ================================================================

    /// <summary>
    /// cursor 위치 = DIB 정중앙. ShowAtCenter 가 monitor DPI + bbox 직접 계산해 정확한 좌상단 좌표 set.
    /// Render 가 같은 DPI 로 DIB 생성 → race 없음. 윈도우는 WS_VISIBLE 영구 박혀있어 ShowWindow 호출
    /// 불요 — Render 의 UpdateLayeredWindow 가 alpha=255 (디폴트) 로 표시.
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
        }

        _isVisible = true;
    }

    /// <summary>
    /// 커서 윈도우를 <see cref="Win32Constants.HWND_TOPMOST"/> 로 (재)설정. 첫 표시
    /// (<see cref="RenderAtCursor"/>) + 주기 재적용 (<see cref="MaybeReassertTopmost"/>) 단일 코드 경로.
    /// <para>
    /// cursor 윈도우는 생성 시 <c>WS_EX_TOPMOST</c> 없이 일반 z-order 로 시작 (Program.Bootstrap —
    /// cursor 첫 UpdateLayeredWindow 가 DWM 합성에서 다른 topmost (Shell_TrayWnd) 재정렬 → foreground
    /// 잠시 변경 → 메인 인디 SystemFilter hide 회귀 방지). <c>SWP_NOSENDCHANGING</c> 으로 다른 윈도우에
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
    }

    /// <summary>
    /// AppConfig + IME 상태 + CAPS 토글 → CursorStyle 합성. 한글/비한글 IME 는 같은 카테고리로 묶어
    /// CAPS Outer 색상이 영문 색상이 되고, 영문 IME 일 때만 CAPS 가 한글 색상 (인터뷰 결정).
    /// </summary>
    private static CursorStyle BuildStyle(AppConfig config, ImeState state, bool capsOn)
    {
        string currentBg = state switch
        {
            ImeState.Hangul => config.HangulBg,
            ImeState.English => config.EnglishBg,
            _ => config.NonKoreanBg
        };
        string capsBg = state == ImeState.English ? config.HangulBg : config.EnglishBg;

        uint innerArgb = HexToArgb(currentBg);
        uint outerArgb = HexToArgb(capsBg);

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
    /// "#RRGGBB" → 0xFFRRGGBB (A=255 불투명 100%). 잘못된 형식은 검정 (0xFF000000).
    /// </summary>
    private static uint HexToArgb(string hex)
    {
        var (r, g, b) = ColorHelper.HexToRgb(hex);
        return ((uint)0xFF << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

}
