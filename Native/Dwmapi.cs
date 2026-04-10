using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Dwmapi
{
    /// <summary>DWM이 실제로 합성하는 "보이는" 프레임 경계. GetWindowRect는 invisible resize border를 포함하므로 시각적 정렬에는 이 값을 써야 한다.</summary>
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(
        IntPtr hwnd,
        uint dwAttribute,
        out RECT pvAttribute,
        uint cbAttribute);

    /// <summary>DWM extended frame bounds로 시각적 프레임 rect 조회. 실패 시 GetWindowRect로 폴백.</summary>
    public static bool TryGetVisibleFrame(IntPtr hwnd, out RECT frame)
    {
        frame = default;
        int hr = DwmGetWindowAttribute(
            hwnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            out frame,
            (uint)Marshal.SizeOf<RECT>());
        if (hr == 0) return true;
        return User32.GetWindowRect(hwnd, out frame);
    }
}
