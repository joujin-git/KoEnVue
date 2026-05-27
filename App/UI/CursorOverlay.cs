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

    // Initialize 호출 시점 tick. 부팅 직후 메인 인디 fade race 회피 위해 첫 N ms 동안 cursor
    // 표시 skip (사용자 보고: cursor enable 시 부팅 깜박임 회귀 — cursor motion timer 첫 발화가
    // 메인 인디 fade race trigger 가설). HandleConfigChanged 의 OFF→ON 토글 시에도 리셋.
    // 500ms grace 가 부족했음 (사용자 2차 보고: 메인 인디 1초 후 사라짐) — cursor RenderAtCursor
    // 의 ShadeDib (2x2 supersampling) 메인 스레드 점유가 메인 인디 fade tick 누락 trigger 가설.
    // 1500ms 로 늘림: 메인 인디 fade-in 150ms + EventDisplayDuration 일부 + 안정화 완료 후 cursor 진입.
    private static long _bootTick;
    private const int BootGracePeriodMs = 1500;

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
        _bootTick = Environment.TickCount64;
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

        // 부팅 grace period — Initialize 후 BootGracePeriodMs (500ms) 동안 cursor 표시 skip.
        // cursor enable 부팅에서 메인 인디가 부팅 깜박임 회귀 보고 (cursor motion timer 첫 발화가
        // detection thread 의 메시지 폭주 + 메인 인디 fade race trigger 가설). 500ms 면 detection
        // thread 의 첫 80ms 폴링 + 메인 인디 SnapToTargetAlpha 안정화 완료.
        if (Environment.TickCount64 - _bootTick < BootGracePeriodMs) return;

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
    /// cursor 위치 = DIB 정중앙. ShowAtCenter 가 monitor DPI + bbox 직접 계산해 정확한 좌상단 좌표 set.
    /// Render 가 같은 DPI 로 DIB 생성 → race 없음. 윈도우는 WS_VISIBLE 영구 박혀있어 ShowWindow 호출
    /// 불요 — Render 의 UpdateLayeredWindow 가 alpha=255 (디폴트) 로 표시.
    /// <para>
    /// <b>첫 표시 시 명시 HWND_TOPMOST set</b> — cursor 윈도우는 생성 시 WS_EX_TOPMOST 없이 일반 z-order
    /// 로 시작 (사용자 보고: cursor enable 부팅 시 cursor 첫 UpdateLayeredWindow 가 DWM 합성에서 다른
    /// topmost 윈도우 (Shell_TrayWnd) 재정렬 → foreground 잠시 변경 → 메인 인디 SystemFilter hide 회귀).
    /// 첫 가시화 시 SWP_NOSENDCHANGING + SWP_NOACTIVATE 로 명시 topmost set — 다른 윈도우 z-order
    /// 변경 알림 차단.
    /// </para>
    /// </summary>
    private static void RenderAtCursor(POINT cursor)
    {
        if (_engine is null) return;

        _engine.ShowAtCenter(cursor.X, cursor.Y, _currentStyle);
        _engine.Render(_currentStyle);

        if (!_isVisible)
        {
            User32.SetWindowPos(_engine.Hwnd, Win32Constants.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE
                | Win32Constants.SWP_NOACTIVATE | Win32Constants.SWP_NOSENDCHANGING);
        }

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
