using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

/// <summary>
/// Windows Terminal Services session notification API.
/// WTSRegisterSessionNotification 으로 창에 세션 이벤트(WM_WTSSESSION_CHANGE) 수신을 등록하면
/// 잠금(WTS_SESSION_LOCK)·해제(WTS_SESSION_UNLOCK)·로그오프 등을 받을 수 있다.
/// 폴링 없이 이벤트 기반이라 HideOnLockScreen 구현에 적합.
/// </summary>
internal static partial class Wtsapi32
{
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
