using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace KoEnVue.Native;

/// <summary>
/// IVirtualDesktopManager COM 인터페이스.
/// 가상 데스크톱 소속 여부 확인에 사용.
/// </summary>
[GeneratedComInterface]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
internal partial interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow,
        [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
}
