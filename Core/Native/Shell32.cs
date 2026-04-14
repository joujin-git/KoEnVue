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
    /// 반환값은 HINSTANCE 타입이지만 의미상 정수 — <c>&lt;= 32</c> 이면 실패.
    /// 호출자는 rc &lt;= 32 를 감지해 로그로 기록만 하고 사용자 팝업은 띄우지 않는 것이 관행
    /// (브라우저 실행 실패는 사용자가 대응할 수 있는 게 없음).
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
