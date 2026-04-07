using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Imm32
{
    [LibraryImport("imm32.dll")]
    internal static partial IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    internal static partial IntPtr ImmGetContext(IntPtr hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ImmGetConversionStatus(IntPtr hIMC, out uint lpfdwConversion, out uint lpfdwSentence);
}
