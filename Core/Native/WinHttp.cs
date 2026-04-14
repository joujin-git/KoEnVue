using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KoEnVue.Core.Native;

/// <summary>
/// WinHTTP P/Invoke bindings.
/// HttpClient 의존성을 들이면 NativeAOT publish 출력이 +2.5MB 이상 늘어나므로,
/// 단방향 GET 한 건만 필요한 본 프로젝트에서는 winhttp.dll 직접 호출이 훨씬 작다.
/// </summary>
internal static partial class WinHttp
{
    // === 상수 ===

    internal const uint WINHTTP_ACCESS_TYPE_DEFAULT_PROXY = 0;
    internal const uint WINHTTP_ACCESS_TYPE_NO_PROXY = 1;
    internal const uint WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY = 4; // Win8.1+

    internal const ushort INTERNET_DEFAULT_HTTPS_PORT = 443;
    internal const ushort INTERNET_DEFAULT_HTTP_PORT = 80;

    internal const uint WINHTTP_FLAG_SECURE = 0x00800000;

    internal const uint WINHTTP_QUERY_STATUS_CODE = 19;
    internal const uint WINHTTP_QUERY_FLAG_NUMBER = 0x20000000;

    internal const uint WINHTTP_ADDREQ_FLAG_ADD = 0x20000000;
    internal const uint WINHTTP_ADDREQ_FLAG_REPLACE = 0x80000000;

    internal const uint WINHTTP_NO_HEADER_INDEX = 0;

    // === LibraryImport 바인딩 ===

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpOpen", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr WinHttpOpen(
        string? pszAgentW,
        uint dwAccessType,
        IntPtr pszProxyW,
        IntPtr pszProxyBypassW,
        uint dwFlags);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpConnect", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr WinHttpConnect(
        IntPtr hSession,
        string pswzServerName,
        ushort nServerPort,
        uint dwReserved);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpOpenRequest", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr WinHttpOpenRequest(
        IntPtr hConnect,
        string pwszVerb,
        string pwszObjectName,
        string? pwszVersion,
        string? pwszReferrer,
        IntPtr ppwszAcceptTypes,
        uint dwFlags);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpAddRequestHeaders", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpAddRequestHeaders(
        IntPtr hRequest,
        string pwszHeaders,
        uint dwHeadersLength,
        uint dwModifiers);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpSendRequest", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpSendRequest(
        IntPtr hRequest,
        IntPtr lpszHeaders,
        uint dwHeadersLength,
        IntPtr lpOptional,
        uint dwOptionalLength,
        uint dwTotalLength,
        IntPtr dwContext);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpReceiveResponse", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpReceiveResponse(IntPtr hRequest, IntPtr lpReserved);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpQueryHeaders", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpQueryHeaders(
        IntPtr hRequest,
        uint dwInfoLevel,
        IntPtr pwszName,
        ref uint lpBuffer,
        ref uint lpdwBufferLength,
        ref uint lpdwIndex);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpQueryDataAvailable", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpQueryDataAvailable(IntPtr hRequest, out uint lpdwNumberOfBytesAvailable);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpReadData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WinHttpReadData(
        IntPtr hRequest,
        byte* lpBuffer,
        uint dwNumberOfBytesToRead,
        out uint lpdwNumberOfBytesRead);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpCloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpCloseHandle(IntPtr hInternet);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpSetTimeouts", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinHttpSetTimeouts(
        IntPtr hInternet,
        int nResolveTimeout,
        int nConnectTimeout,
        int nSendTimeout,
        int nReceiveTimeout);
}

/// <summary>
/// WinHTTP HINTERNET 핸들 래퍼. ReleaseHandle 에서 WinHttpCloseHandle 호출.
/// 세션/연결/요청 모두 같은 HINTERNET 타입이므로 단일 클래스로 처리.
/// </summary>
internal sealed class SafeWinHttpHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeWinHttpHandle() : base(ownsHandle: true) { }
    public SafeWinHttpHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        return WinHttp.WinHttpCloseHandle(handle);
    }
}
