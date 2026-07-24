using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class Kernel32
{
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

    // === 경로 / reparse (PortablePath.SanitizeLogPath) ===

    /// <summary>GetFileAttributesW 실패 시 반환값.</summary>
    internal const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFileAttributesW(string lpFileName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// 핸들이 가리키는 최종 경로(junction/symlink 해석 후). 반환값은 기록한 문자 수(널 제외).
    /// 버퍼 부족 시 필요 크기 반환(널 포함) — 호출자가 재시도.
    /// </summary>
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
