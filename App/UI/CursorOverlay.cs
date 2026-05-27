using KoEnVue.App.Models;
using KoEnVue.Core.Color;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

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

        int dx = Math.Abs(cursor.X - _lastCursorX);
        int dy = Math.Abs(cursor.Y - _lastCursorY);
        bool moving = (dx + dy) > _config.CursorMotionThresholdPx;

        _lastCursorX = cursor.X;
        _lastCursorY = cursor.Y;

        if (_config.CursorAlwaysShow)
        {
            // 항상 표시 모드 — 가시화 보장 + 매 tick 위치 추종. idle 검출/숨김 skip.
            RenderAtCursor(cursor);
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
        if (_isVisible) return; // 이미 가시 — 정지 시점에 잡혀 있음

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
    }

    // ================================================================
    // 내부
    // ================================================================

    /// <summary>
    /// cursor 위치 = DIB 정중앙. Show 가 monitor DPI 갱신 + 좌표 캐시 → Render 가 DIB 재생성/픽셀 셰이딩 +
    /// UpdateLayeredWindow. flip-flop 가드가 동일 스타일 + 동일 DPI 케이스에서 DIB 재생성 skip.
    /// </summary>
    private static void RenderAtCursor(POINT cursor)
    {
        if (_engine is null) return;

        // PrepareResources 가 호출됐으므로 GetBaseSize 가 유효 — 다만 DPI 변경 직후 0 가능.
        var (w, _) = _engine.GetBaseSize();
        int halfBbox = w / 2;
        // halfBbox 가 0 이면 첫 진입 또는 DPI 무효화 직후 — Show 가 DPI 갱신 후 Render 가 EnsureDib 호출.
        // 그 시점에 다시 GetBaseSize 호출하면 valid. 단순화를 위해 첫 진입은 PrepareResources 후속
        // Render 로 처리되도록 함.

        _engine.Show(cursor.X - halfBbox, cursor.Y - halfBbox);
        _engine.Render(_currentStyle);

        // DPI 갱신 후 base size 바뀌었으면 좌표 재보정. Render 가 EnsureDib 호출했으므로 GetBaseSize 가
        // 새 값. 차이가 있으면 UpdatePosition 으로 좌상단 재조정.
        var (w2, _) = _engine.GetBaseSize();
        if (w2 != w && w2 > 0)
        {
            int halfBbox2 = w2 / 2;
            _engine.UpdatePosition(cursor.X - halfBbox2, cursor.Y - halfBbox2);
        }

        // 첫 가시화 시 SW_SHOW 명시 호출. LayeredCursorBase.Show 는 좌표/DPI 캐시만 갱신하고
        // ShowWindow 안 부름 (dev-notes/2026-05-20 가설 A: Render 전 SW_SHOW 가 layered window 비트맵
        // 없이 visible 캐싱 위험 → 호출자가 Render 후 명시 SW_SHOW 패턴이 안전). 메인 인디 (Animation.cs)
        // 와 동일 SW_SHOW 사용.
        if (!_isVisible)
            User32.ShowWindow(_engine.Hwnd, Win32Constants.SW_SHOW);

        _isVisible = true;
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
