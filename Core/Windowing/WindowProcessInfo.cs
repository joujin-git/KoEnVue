using System.Runtime.InteropServices;
using KoEnVue.Core.Native;
using KoEnVue.Core.Logging;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// 윈도우 HWND로부터 클래스명/프로세스명을 조회하는 헬퍼.
/// Detector/Config 레이어 모두에서 사용 가능하도록 Utils로 이관.
/// </summary>
internal static class WindowProcessInfo
{
    private const string ApplicationFrameHost = "ApplicationFrameHost";

    // EnumChildWindows 콜백용 스레드별 브리지 필드.
    // GetProcessName은 메인 스레드와 감지 스레드 양쪽에서 호출되므로
    // [ThreadStatic]으로 스레드 안전성 확보.
    [ThreadStatic] private static string? t_resolvedUwpName;
    [ThreadStatic] private static uint t_frameHostPid;

    // GetClassName 호출당 char[256] 신규 할당을 피하기 위한 스레드별 재사용 버퍼.
    // GetClassName 도 메인/감지 스레드 양쪽에서 호출되므로 [ThreadStatic].
    [ThreadStatic] private static char[]? t_classNameBuffer;

    /// <summary>
    /// 윈도우 클래스명 조회.
    /// </summary>
    public static string GetClassName(IntPtr hwnd)
    {
        char[] buffer = t_classNameBuffer ??= new char[Win32Constants.MAX_CLASS_NAME];
        int len = User32.GetClassNameW(hwnd, buffer, Win32Constants.MAX_CLASS_NAME);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    /// <summary>
    /// HWND로부터 프로세스 이름 조회.
    /// UWP 앱은 ApplicationFrameHost가 윈도우 프레임을 소유하므로,
    /// 자식 윈도우를 탐색하여 실제 앱 프로세스 이름을 반환한다.
    /// </summary>
    public static string GetProcessName(IntPtr hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        string name = GetProcessName(processId);

        if (name == ApplicationFrameHost)
        {
            string resolved = ResolveUwpProcessName(hwnd, processId);
            if (resolved.Length > 0)
                return resolved;
        }

        return name;
    }

    /// <summary>
    /// 프로세스 ID로부터 프로세스 이름 조회.
    /// </summary>
    public static string GetProcessName(uint processId)
    {
        if (processId == 0) return string.Empty;

        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById((int)processId);
            return proc.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException
                                     or InvalidOperationException
                                     or System.ComponentModel.Win32Exception)
        {
            // ArgumentException: PID 누락/만료 (가장 흔한 경로)
            // InvalidOperationException: ProcessName 게터가 프로세스 상태 재평가 시 사라진 edge case
            // Win32Exception: 권한 없음 (서비스/시스템 프로세스 접근)
            // 80ms 폴링 핫패스이므로 Debug 레벨 유지 (Info/Warning이면 스팸 위험)
            Logger.Debug($"GetProcessName failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// ApplicationFrameHost 윈도우의 자식 윈도우를 탐색하여
    /// 프레임 호스트와 다른 PID를 가진 실제 UWP 앱 프로세스 이름을 반환한다.
    /// </summary>
    private static string ResolveUwpProcessName(IntPtr hwnd, uint frameHostPid)
    {
        t_frameHostPid = frameHostPid;
        t_resolvedUwpName = null;

        unsafe
        {
            User32.EnumChildWindows(hwnd, &EnumChildCallback, IntPtr.Zero);
        }

        return t_resolvedUwpName ?? string.Empty;
    }

    /// <summary>
    /// EnumChildWindows 콜백. 프레임 호스트와 다른 PID를 가진 첫 자식의 프로세스명을 캡처.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int EnumChildCallback(IntPtr hwnd, IntPtr lParam)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint childPid);
        if (childPid != 0 && childPid != t_frameHostPid)
        {
            string childName = GetProcessName(childPid);
            if (childName.Length > 0)
            {
                t_resolvedUwpName = childName;
                return 0; // 열거 중단
            }
        }
        return 1; // 계속
    }
}
