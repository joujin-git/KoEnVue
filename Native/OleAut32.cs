using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class OleAut32
{
    [LibraryImport("oleaut32.dll")]
    internal static partial void SysFreeString(IntPtr bstrString);

    // === SafeArray P/Invoke (UIA GetBoundingRectangles SAFEARRAY(double) 처리) ===

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayAccessData(IntPtr psa, out IntPtr ppvData);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayUnaccessData(IntPtr psa);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetUBound(IntPtr psa, uint nDim, out int plUbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetLBound(IntPtr psa, uint nDim, out int plLbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayDestroy(IntPtr psa);
}
