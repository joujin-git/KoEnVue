using System.Text;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Http;

/// <summary>
/// 동기 HTTP GET 1회만 수행하는 최소 클라이언트. 내부적으로 winhttp.dll 직접 호출.
/// <para>
/// .NET <c>HttpClient</c> 를 도입하면 NativeAOT 출력에 약 +2.5MB(전체 +50%) 부담이 발생한다.
/// 본 프로젝트는 업데이트 체크 1회 호출만 필요하므로 winhttp 박막 래퍼로 대체.
/// </para>
/// <para>
/// 기능 한정: GET only, 응답 본문은 UTF-8 텍스트로 가정, 성공 = HTTP 200 + 1바이트 이상의 본문,
/// 그 외(network error / 타임아웃 / non-200 / empty body / 본문 read 실패)는 모두 null 반환.
/// 본 클래스는 절대 throw 하지 않는다 — 호출자는 null 체크만 하면 됨.
/// </para>
/// <para>
/// 핸들 수명은 <see cref="SafeWinHttpHandle"/> 3개(<c>using</c>) 가 소유. 정상/예외 양 경로 모두
/// <c>Dispose → ReleaseHandle → WinHttpCloseHandle</c> 이 보장되므로 수동 <c>try/finally</c> 정리 불필요.
/// </para>
/// </summary>
internal static class HttpClientLite
{
    /// <summary>HTTPS GET 요청을 1회 수행하고 본문 문자열을 반환. 실패 시 null.</summary>
    /// <param name="userAgent">User-Agent 헤더 값. GitHub API 는 UA 누락 시 403 응답.</param>
    /// <param name="host">호스트명. 예: <c>api.github.com</c>.</param>
    /// <param name="path">경로. 예: <c>/repos/owner/repo/releases/latest</c>. 슬래시로 시작.</param>
    /// <param name="extraHeaders">추가 헤더(개행 구분, 각 줄 <c>Name: Value</c> 형식). null 가능.</param>
    /// <param name="timeoutMs">Resolve/Connect/Send/Receive 각 단계에 동일하게 적용할 타임아웃 (ms).
    /// 세션 레벨에 설정하면 파생 핸들이 자동 상속.</param>
    public static string? GetString(string userAgent, string host, string path, string? extraHeaders = null, int timeoutMs = 10_000)
    {
        try
        {
            using var hSession = new SafeWinHttpHandle(
                WinHttp.WinHttpOpen(
                    userAgent,
                    WinHttp.WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0),
                ownsHandle: true);
            if (hSession.IsInvalid) return null;

            // 세션 레벨 타임아웃 — 이후 생성되는 Connect/Request 핸들이 상속하므로 한 번만 호출.
            WinHttp.WinHttpSetTimeouts(
                hSession.DangerousGetHandle(),
                timeoutMs, timeoutMs, timeoutMs, timeoutMs);

            using var hConnect = new SafeWinHttpHandle(
                WinHttp.WinHttpConnect(
                    hSession.DangerousGetHandle(),
                    host,
                    WinHttp.INTERNET_DEFAULT_HTTPS_PORT,
                    0),
                ownsHandle: true);
            if (hConnect.IsInvalid) return null;

            using var hRequest = new SafeWinHttpHandle(
                WinHttp.WinHttpOpenRequest(
                    hConnect.DangerousGetHandle(),
                    "GET",
                    path,
                    null,
                    null,
                    IntPtr.Zero,
                    WinHttp.WINHTTP_FLAG_SECURE),
                ownsHandle: true);
            if (hRequest.IsInvalid) return null;

            IntPtr hReq = hRequest.DangerousGetHandle();

            if (!string.IsNullOrEmpty(extraHeaders))
            {
                if (!WinHttp.WinHttpAddRequestHeaders(
                        hReq,
                        extraHeaders,
                        unchecked((uint)-1),  // -1 = 길이 미지정, WinHTTP 가 null-terminator 까지 자동 계산
                        WinHttp.WINHTTP_ADDREQ_FLAG_ADD))
                {
                    return null;
                }
            }

            if (!WinHttp.WinHttpSendRequest(
                    hReq,
                    IntPtr.Zero, 0,
                    IntPtr.Zero, 0,
                    0,
                    IntPtr.Zero))
            {
                return null;
            }

            if (!WinHttp.WinHttpReceiveResponse(hReq, IntPtr.Zero))
                return null;

            uint statusCode = 0;
            uint statusSize = sizeof(uint);
            uint headerIndex = WinHttp.WINHTTP_NO_HEADER_INDEX;
            if (!WinHttp.WinHttpQueryHeaders(
                    hReq,
                    WinHttp.WINHTTP_QUERY_STATUS_CODE | WinHttp.WINHTTP_QUERY_FLAG_NUMBER,
                    IntPtr.Zero,
                    ref statusCode,
                    ref statusSize,
                    ref headerIndex))
            {
                return null;
            }

            if (statusCode != 200) return null;

            return ReadResponseBody(hReq);
        }
        catch (Exception)
        {
            // 정책 항목 2(타입 좁히기 불가능): WinHTTP 호출 자체는 throw 하지 않으나
            // string marshalling 경로에서 OOM/AccessViolation 이 가능. 단방향 GET 1회용
            // 코드이므로 모든 예외를 흡수하고 null 반환 — 호출자는 어떤 실패도 동일하게 다룸.
            return null;
        }
    }

    private static unsafe string? ReadResponseBody(IntPtr hRequest)
    {
        var ms = new MemoryStream();
        const int chunkSize = 8192;
        byte[] buffer = new byte[chunkSize];

        while (true)
        {
            if (!WinHttp.WinHttpQueryDataAvailable(hRequest, out uint available))
                return null;
            if (available == 0) break;

            uint toRead = available > (uint)buffer.Length ? (uint)buffer.Length : available;
            uint bytesRead;
            fixed (byte* p = buffer)
            {
                if (!WinHttp.WinHttpReadData(hRequest, p, toRead, out bytesRead))
                    return null;
            }

            if (bytesRead == 0) break;
            ms.Write(buffer, 0, (int)bytesRead);

            // 응답 크기 상한 256KB — GitHub releases/latest 페이로드는 통상 10KB 미만이므로
            // 넉넉한 한도이자, 악의적으로 거대한 응답이 올 경우의 메모리 폭주 방지 가드.
            if (ms.Length > 256 * 1024) return null;
        }

        // 0바이트 바디는 200 OK 라도 호출자 관점에서 실패와 동일하게 처리 — GetString 계약상 null.
        if (ms.Length == 0) return null;
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
