using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetLastError();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateMutexW(IntPtr lpMutexAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseMutex(IntPtr hMutex);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    // MulDiv는 kernel32.dll 소속 (gdi32.dll 아님!)
    [LibraryImport("kernel32.dll")]
    internal static partial int MulDiv(int nNumber, int nNumerator, int nDenominator);

    // === 시스템 언어 ===

    [LibraryImport("kernel32.dll")]
    internal static partial ushort GetUserDefaultUILanguage();
}
