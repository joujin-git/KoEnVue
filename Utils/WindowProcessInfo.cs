using KoEnVue.Native;

namespace KoEnVue.Utils;

/// <summary>
/// 윈도우 HWND로부터 클래스명/프로세스명을 조회하는 헬퍼.
/// Detector/Config 레이어 모두에서 사용 가능하도록 Utils로 이관.
/// </summary>
internal static class WindowProcessInfo
{
    /// <summary>
    /// 윈도우 클래스명 조회.
    /// </summary>
    public static string GetClassName(IntPtr hwnd)
    {
        char[] buffer = new char[Win32Constants.MAX_CLASS_NAME];
        int len = User32.GetClassNameW(hwnd, buffer, Win32Constants.MAX_CLASS_NAME);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    /// <summary>
    /// HWND로부터 프로세스 이름 조회.
    /// </summary>
    public static string GetProcessName(IntPtr hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        return GetProcessName(processId);
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
}
