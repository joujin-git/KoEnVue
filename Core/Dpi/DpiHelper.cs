using KoEnVue.Core.Native;

namespace KoEnVue.Core.Dpi;

/// <summary>
/// DPI 조회 및 스케일링 유틸리티.
/// P4 원칙: 모든 DPI 관련 로직은 이 1곳에서만 구현.
/// </summary>
internal static class DpiHelper
{
    /// <summary>DPI 기준값. 100% 스케일링 = 96 DPI.</summary>
    public const int BASE_DPI = 96;

    /// <summary>
    /// 기본값에 DPI 스케일을 적용하여 정수 픽셀로 변환.
    /// 반드시 Math.Round 사용. (int) 절삭은 계통적 과소 스케일링 유발(F-S05).
    /// </summary>
    public static int Scale(int baseValue, double dpiScale)
    {
        return (int)Math.Round(baseValue * dpiScale);
    }

    /// <summary>
    /// double 오프셋에 DPI 스케일 적용.
    /// </summary>
    public static int Scale(double baseValue, double dpiScale)
    {
        return (int)Math.Round(baseValue * dpiScale);
    }

    /// <summary>
    /// 특정 모니터의 DPI 스케일 배율을 조회한다.
    /// MonitorFromPoint -> GetDpiForMonitor -> dpiX / BASE_DPI.
    /// </summary>
    public static double GetScale(IntPtr hMonitor)
    {
        int hr = Shcore.GetDpiForMonitor(hMonitor, Win32Constants.MDT_EFFECTIVE_DPI,
            out uint dpiX, out uint _);
        if (hr != Win32Constants.S_OK || dpiX == 0) return 1.0;  // 실패 시 100% 기본값
        return dpiX / (double)BASE_DPI;
    }

    /// <summary>
    /// 스크린 좌표에서 해당 모니터의 작업 영역(rcWork)을 조회한다.
    /// MonitorFromPoint -> GetMonitorInfoW -> rcWork.
    /// 작업표시줄 제외된 실제 사용 가능 영역.
    /// </summary>
    public static RECT GetWorkArea(IntPtr hMonitor)
    {
        var monitorInfo = new MONITORINFOEXW();
        monitorInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMonitor, ref monitorInfo);
        return monitorInfo.rcWork;
    }

    /// <summary>
    /// 모니터의 raw DPI 값을 반환. HFONT 생성 시 dpiY 필요.
    /// </summary>
    public static (uint dpiX, uint dpiY) GetRawDpi(IntPtr hMonitor)
    {
        int hr = Shcore.GetDpiForMonitor(hMonitor, Win32Constants.MDT_EFFECTIVE_DPI,
            out uint dpiX, out uint dpiY);
        if (hr != Win32Constants.S_OK || dpiX == 0) return ((uint)BASE_DPI, (uint)BASE_DPI);
        return (dpiX, dpiY);
    }

    /// <summary>
    /// 좌표가 속한 모니터 핸들을 반환한다.
    /// MONITOR_DEFAULTTONEAREST: 가상 데스크톱 밖이면 가장 가까운 모니터.
    /// </summary>
    public static IntPtr GetMonitorFromPoint(int x, int y)
    {
        return User32.MonitorFromPoint(new POINT(x, y), Win32Constants.MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>
    /// 모니터의 전체 영역(rcMonitor)을 반환한다.
    /// </summary>
    public static RECT GetMonitorRect(IntPtr hMonitor)
    {
        var monitorInfo = new MONITORINFOEXW();
        monitorInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMonitor, ref monitorInfo);
        return monitorInfo.rcMonitor;
    }
}
