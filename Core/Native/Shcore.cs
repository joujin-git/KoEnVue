using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class Shcore
{
    /// <summary>
    /// Per-Monitor DPI 조회. MDT_EFFECTIVE_DPI = 0.
    /// </summary>
    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hmonitor, uint dpiType,
        out uint dpiX, out uint dpiY);
}
