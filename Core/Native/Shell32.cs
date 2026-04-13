using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class Shell32
{
    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
}
