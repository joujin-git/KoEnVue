using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class Shell32
{
    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    /// <summary>
    /// 셸 verb 실행. URL/파일을 기본 핸들러로 열 때 사용.
    /// 브라우저 오픈은 <c>ShellExecuteW(IntPtr.Zero, "open", url, null, null, SW_SHOWNORMAL)</c>.
    /// 반환값이 32 이하면 실패. 본 프로젝트는 반환값을 무시(silent fail).
    /// </summary>
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr ShellExecuteW(
        IntPtr hwnd,
        string? lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);
}
