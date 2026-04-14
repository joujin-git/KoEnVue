using KoEnVue.Core.Native;

namespace KoEnVue.App.UI;

/// <summary>
/// 커스텀 윈도우 메시지 정의.
/// 감지 스레드와 훅 콜백이 PostMessage로 메인 스레드에 이벤트를 전달할 때 사용.
/// </summary>
internal static class AppMessages
{
    // --- WM_APP 기반 커스텀 메시지 ---

    /// <summary>
    /// IME 상태 변경.
    /// wParam: (IntPtr)(int)ImeState enum 값
    /// lParam: 0
    /// </summary>
    public const uint WM_IME_STATE_CHANGED = Win32Constants.WM_APP + 1;

    /// <summary>
    /// 포커스 윈도우 변경.
    /// wParam: 새 hwndFocus (IntPtr)
    /// lParam: 0
    /// </summary>
    public const uint WM_FOCUS_CHANGED = Win32Constants.WM_APP + 2;

    /// <summary>
    /// 포그라운드 윈도우 위치 갱신 (타이틀바 배치용).
    /// wParam: hwndForeground (IntPtr)
    /// lParam: 0
    /// </summary>
    public const uint WM_POSITION_UPDATED = Win32Constants.WM_APP + 3;

    /// <summary>
    /// 인디케이터 즉시 숨기기.
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_HIDE_INDICATOR = Win32Constants.WM_APP + 4;

    /// <summary>
    /// 설정 변경 감지 (config.json 리로드 또는 트레이 메뉴 변경).
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_CONFIG_CHANGED = Win32Constants.WM_APP + 5;

    // --- Timer IDs (WM_TIMER wParam) ---

    public const nuint TIMER_ID_FADE      = 1;  // ~16ms, 페이드 애니메이션
    public const nuint TIMER_ID_HOLD      = 2;  // one-shot, 유지 시간
    public const nuint TIMER_ID_HIGHLIGHT = 3;  // ~16ms, 강조 스케일
    public const nuint TIMER_ID_TOPMOST   = 4;  // 5000ms, TOPMOST 재적용
    public const nuint TIMER_ID_SLIDE     = 5;  // ~16ms, 슬라이드 위치 보간
    public const nuint TIMER_ID_CAPS      = 6;  // 200ms, CAPS LOCK 토글 폴링 (메인 스레드)

    // --- WM_USER 기반 ---

    /// <summary>
    /// 트레이 아이콘 콜백.
    /// Shell_NotifyIconW의 uCallbackMessage에 설정.
    /// </summary>
    public const uint WM_TRAY_CALLBACK = Win32Constants.WM_USER + 1;
}
