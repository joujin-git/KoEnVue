using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

/// <summary>
/// Advapi32 P/Invoke — process token + SID 조회로 mandatory integrity level 추출.
/// UIPI (User Interface Privilege Isolation) 가 Medium IL → High IL 사이의 윈도우
/// 메시지 (WM_IME_CONTROL 등) 를 차단하므로, KoEnVue 가 admin 콘솔 IME 상태를 잡으려면
/// 자기 IL 을 알아야 한다.
/// </summary>
internal static partial class Advapi32
{
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        IntPtr ProcessHandle,
        uint DesiredAccess,
        out IntPtr TokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        IntPtr TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    [LibraryImport("advapi32.dll")]
    internal static partial IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

    [LibraryImport("advapi32.dll")]
    internal static partial IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    /// <summary>
    /// 현재 프로세스의 mandatory integrity level RID 반환. 실패/미지정 시 0.
    /// 비교는 <see cref="Win32Constants.SECURITY_MANDATORY_HIGH_RID"/> 등과 직접.
    /// caller 가 0 처리 (대부분 Medium 으로 간주하거나 별도 로그).
    /// </summary>
    internal static uint GetCurrentProcessIntegrityLevelRid()
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr pBuffer = IntPtr.Zero;
        try
        {
            // GetCurrentProcess() 의사 핸들 = -1, CloseHandle 불필요.
            IntPtr hCurrentProcess = new IntPtr(-1);
            if (!OpenProcessToken(hCurrentProcess, Win32Constants.TOKEN_QUERY, out hToken))
                return 0;

            // 1차: 필요 버퍼 크기 조회. GetTokenInformation 은 ERROR_INSUFFICIENT_BUFFER (122)
            // 로 반환하면서 dwLength 에 필요 크기를 채운다. false 반환은 정상 경로.
            GetTokenInformation(hToken, Win32Constants.TokenIntegrityLevel,
                IntPtr.Zero, 0, out uint dwLength);
            if (dwLength == 0)
                return 0;

            pBuffer = Marshal.AllocHGlobal((int)dwLength);
            if (!GetTokenInformation(hToken, Win32Constants.TokenIntegrityLevel,
                    pBuffer, dwLength, out _))
                return 0;

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label } 의 첫 필드는
            // SID_AND_ATTRIBUTES.Sid (PSID = IntPtr). 64-bit 에서 첫 8 바이트 = pSid.
            IntPtr pSid = Marshal.ReadIntPtr(pBuffer);
            if (pSid == IntPtr.Zero)
                return 0;

            // GetSidSubAuthorityCount 가 byte* 반환 — Marshal.ReadByte 로 단일 바이트 추출.
            IntPtr pSubCount = GetSidSubAuthorityCount(pSid);
            if (pSubCount == IntPtr.Zero)
                return 0;
            byte subCount = Marshal.ReadByte(pSubCount);
            if (subCount == 0)
                return 0;

            // 마지막 SubAuthority = mandatory integrity RID. GetSidSubAuthority 가 uint* 반환.
            IntPtr pRid = GetSidSubAuthority(pSid, (uint)(subCount - 1));
            if (pRid == IntPtr.Zero)
                return 0;
            return (uint)Marshal.ReadInt32(pRid);
        }
        finally
        {
            if (pBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pBuffer);
            if (hToken != IntPtr.Zero) Kernel32.CloseHandle(hToken);
        }
    }
}
