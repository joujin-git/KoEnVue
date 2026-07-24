using KoEnVue.App.Config;
using KoEnVue.App.Messaging;
using KoEnVue.App.UI;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue;

/// <summary>
/// WM_TIMER 위임 · CAPS LOCK 폴링 · 커서 헤일로 lifecycle.
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// 커서 헤일로 lazy 활성화 — MainImpl 부팅 (config.CursorIndicatorEnabled=true) 또는
    /// HandleConfigChanged 의 OFF→ON 분기에서 호출. 윈도우 생성 → CursorOverlay.Initialize →
    /// 모션 폴링 타이머 등록 (50ms 또는 16ms).
    /// </summary>
    private static void EnableCursorOverlay()
    {
        _hwndCursorOverlay = CreateCursorOverlayWindow();
        if (_hwndCursorOverlay == IntPtr.Zero)
        {
            Logger.Warning("Cursor overlay window creation failed; cursor halo disabled");
            return;
        }
        CursorOverlay.Initialize(_hwndCursorOverlay, _hwndMain, _config, _lastImeState, _lastCapsLockState);
        User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_CURSOR_MOTION,
            _config.CursorAlwaysShow ? DefaultConfig.CursorAlwaysPollMs : DefaultConfig.CursorMotionPollMs,
            IntPtr.Zero);
    }

    /// <summary>
    /// 커서 헤일로 비활성화 — HandleConfigChanged 의 ON→OFF 분기에서 호출. 타이머 해제 + 엔진 dispose +
    /// 윈도우 파괴. _hwndCursorOverlay = IntPtr.Zero 로 리셋 (lazy 재생성 게이트).
    /// </summary>
    private static void DisableCursorOverlay()
    {
        User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_CURSOR_MOTION);
        CursorOverlay.Dispose();
        if (_hwndCursorOverlay != IntPtr.Zero)
        {
            User32.DestroyWindow(_hwndCursorOverlay);
            _hwndCursorOverlay = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 커서 헤일로 lifecycle 3 분기 (OFF→ON / ON→OFF / ON 유지 + 값 변경). HandleConfigChanged
    /// (config.json 리로드 경로) + HandleMenuCommand 람다 (트레이 메뉴 즉시 적용 경로) 양쪽에서
    /// 공유. <c>_config</c> 는 호출 전 새 값으로 갱신돼 있어야 한다.
    /// <para>
    /// HandleMenuCommand 람다가 직접 본 헬퍼를 호출해야 하는 이유: 람다 내부의 Settings.Save 는
    /// mtime self-bump 로 WM_CONFIG_CHANGED 를 차단 (감지 스레드의 mtime 폴러가 본인 변경을 다시
    /// 알리지 않도록). 따라서 HandleConfigChanged 가 호출 안 되고 cursor lifecycle 분기도 자동
    /// 진입 안 한다 — 람다가 직접 호출 필수.
    /// </para>
    /// </summary>
    private static void ApplyCursorConfigChange()
    {
        if (_config.CursorIndicatorEnabled && _hwndCursorOverlay == IntPtr.Zero)
        {
            EnableCursorOverlay();
        }
        else if (!_config.CursorIndicatorEnabled && _hwndCursorOverlay != IntPtr.Zero)
        {
            DisableCursorOverlay();
        }
        else if (_config.CursorIndicatorEnabled)
        {
            CursorOverlay.HandleConfigChanged(_config);
            User32.KillTimer(_hwndMain, AppMessages.TIMER_ID_CURSOR_MOTION);
            User32.SetTimer(_hwndMain, AppMessages.TIMER_ID_CURSOR_MOTION,
                _config.CursorAlwaysShow ? DefaultConfig.CursorAlwaysPollMs : DefaultConfig.CursorMotionPollMs,
                IntPtr.Zero);
        }
    }

    private static void HandleTimer(IntPtr timerId)
    {
        Animation.HandleTimer((nuint)(nint)timerId, _config);
    }

    /// <summary>
    /// 메인 스레드 WM_TIMER(TIMER_ID_CAPS) 핸들러 — CAPS LOCK 토글 상태 폴링.
    /// 토글 비트만 변경됐을 때 Overlay._capsLockOn 필드를 갱신하고 인디가 가시 상태면 즉시 재렌더.
    /// 인디가 숨겨져 있으면 필드만 갱신하고 재렌더는 다음 표시 시점으로 지연된다.
    /// </summary>
    private static void HandleCapsLockTimer()
    {
        bool current = (User32.GetKeyState(Win32Constants.VK_CAPITAL) & 1) != 0;
        if (current == _lastCapsLockState) return;

        _lastCapsLockState = current;
        Logger.Debug($"CapsLock: {(current ? "ON" : "OFF")}");
        Overlay.SetCapsLock(current);
        if (_indicatorVisible)
            Overlay.UpdateColor(_lastImeState, ResolveCurrent());

        // 커서 헤일로도 CAPS 토글에 따라 Outer 원 표시/숨김 + 색상 갱신
        if (_config.CursorIndicatorEnabled)
            CursorOverlay.SetCapsLock(current);
    }
}
