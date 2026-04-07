using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class OleAut32
{
    [LibraryImport("oleaut32.dll")]
    internal static partial void SysFreeString(IntPtr bstrString);
}
