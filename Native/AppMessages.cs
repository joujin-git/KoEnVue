namespace KoEnVue.Native;

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
    /// 캐럿 위치 갱신.
    /// wParam: x 좌표 (signed int -> IntPtr). 스크린 좌표.
    /// lParam: y 좌표 (signed int -> IntPtr). 스크린 좌표.
    /// </summary>
    public const uint WM_CARET_UPDATED = Win32Constants.WM_APP + 3;

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

    // --- WM_USER 기반 ---

    /// <summary>
    /// 트레이 아이콘 콜백.
    /// Shell_NotifyIconW의 uCallbackMessage에 설정.
    /// </summary>
    public const uint WM_TRAY_CALLBACK = Win32Constants.WM_USER + 1;
}
