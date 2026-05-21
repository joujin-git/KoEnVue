using System.Runtime.InteropServices;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Shell;

/// <summary>
/// <see cref="Shell32.ShellExecuteW"/> <c>open</c> verb 호출 + <c>rc &lt;= 32</c> 실패 검출 + 로깅의
/// 단일 진입점. URL/파일 경로 모두 동일한 패턴이라 호출처마다 중복되던 로직을 모은다.
/// <para>
/// 이름이 <c>Open</c> 인 이유 — 실 구현은 ShellExecuteW 호출 후 즉시 반환하는 동기 호출이며
/// 진짜 비동기가 아니다 (<c>OpenAsync</c> 처럼 명명하면 호출자가 await 가능을 기대하게 됨).
/// </para>
/// </summary>
internal static class UriLauncher
{
    /// <summary>
    /// URI 또는 파일 경로를 OS 기본 핸들러로 연다. 실패(<c>rc &lt;= 32</c>) 시 Warning 로깅 후 false.
    /// 사용자 팝업은 띄우지 않는다 (브라우저 실행 실패는 사용자가 대응할 수 없음).
    /// </summary>
    internal static bool Open(string uriOrPath)
        => Open(uriOrPath, parameters: null, label: uriOrPath);

    /// <summary>
    /// 실행 파일 + 인자 형태로 호출한다 (예: <c>notepad.exe "C:\path\config.json"</c>).
    /// 인자에 공백이 있으면 호출자가 사전에 따옴표로 감싸 전달.
    /// </summary>
    internal static bool Open(string file, string parameters)
        => Open(file, parameters: parameters, label: $"{file} {parameters}");

    private static bool Open(string file, string? parameters, string label)
    {
        IntPtr result = Shell32.ShellExecuteW(
            IntPtr.Zero,
            "open",
            file,
            parameters,
            null,
            Win32Constants.SW_SHOWNORMAL);

        // ShellExecuteW 의 반환값은 HINSTANCE 이지만 의미상 정수: <= 32 이면 실패.
        if ((long)result <= 32)
        {
            // rc 자체가 SE_ERR_* 코드를 담는 게 일반적이지만, 일부 헬퍼/쉘 확장은 SetLastError 도
            // 함께 세팅한다 — 진단 정보가 한 곳에 더 있는 게 디버깅에 도움.
            int err = Marshal.GetLastPInvokeError();
            LogProvider.Sink?.Warning($"ShellExecuteW failed for {label} (rc={(long)result}, error={err})");
            return false;
        }
        LogProvider.Sink?.Info($"Opened: {label}");
        return true;
    }
}
