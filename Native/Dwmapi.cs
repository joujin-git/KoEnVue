using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Dwmapi
{
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(
        IntPtr hwnd,
        uint dwAttribute,
        out RECT pvAttribute,
        uint cbAttribute);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(
        IntPtr hwnd,
        uint dwAttribute,
        out uint pvAttribute,
        uint cbAttribute);

    /// <summary>DWM extended frame bounds로 시각적 프레임 rect 조회. 실패 시 GetWindowRect로 폴백.</summary>
    public static bool TryGetVisibleFrame(IntPtr hwnd, out RECT frame)
    {
        frame = default;
        int hr = DwmGetWindowAttribute(
            hwnd,
            Win32Constants.DWMWA_EXTENDED_FRAME_BOUNDS,
            out frame,
            (uint)Marshal.SizeOf<RECT>());
        if (hr == 0) return true;
        return User32.GetWindowRect(hwnd, out frame);
    }

    /// <summary>
    /// 창이 cloaked 상태인지(가상 데스크톱 숨김, UWP suspend 등) 조회.
    /// IsWindowVisible은 true를 반환하지만 실제로는 화면에 표시되지 않는 창을 걸러낼 때 사용.
    /// </summary>
    public static bool IsCloaked(IntPtr hwnd)
    {
        int hr = DwmGetWindowAttribute(
            hwnd,
            Win32Constants.DWMWA_CLOAKED,
            out uint cloaked,
            sizeof(uint));
        return hr == 0 && cloaked != 0;
    }
}
