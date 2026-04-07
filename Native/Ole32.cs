using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Ole32
{
    /// <summary>
    /// COM 초기화. COINIT_APARTMENTTHREADED = 0x2 (STA, UIA 필수).
    /// </summary>
    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
}
