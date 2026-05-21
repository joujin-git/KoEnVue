using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

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

    /// <summary>
    /// DWM colorization color (사용자 personalization accent) 조회.
    /// <para>
    /// 반환값은 0xAARRGGBB ARGB DWORD. <c>GetSysColor(COLOR_HIGHLIGHT)</c> 의 0x00BBGGRR
    /// (BGR COLORREF) 와는 byte 순서가 다르다.
    /// </para>
    /// <para>
    /// Win11 에서 "제목 표시줄과 창 테두리에 강조색 표시" 옵션이 꺼져 있어도 personalization
    /// accent 변경을 정확히 추적한다 — `COLOR_HIGHLIGHT` 보다 신뢰성이 높은 데이터 소스.
    /// </para>
    /// </summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetColorizationColor(
        out uint pcrColorization,
        [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);

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

    /// <summary>
    /// DWM colorization color 를 RGB 3 채널로 분리해 반환. 호출 실패 또는 DWM composition
    /// 비활성 시 false (호출자는 <c>GetSysColor(COLOR_HIGHLIGHT)</c> 등 폴백 사용).
    /// alpha 채널은 무시 — 인디케이터 배경 색은 별도 opacity 키로 처리.
    /// <para>
    /// DWM 의 ARGB DWORD 는 big-endian byte 순서로 0xAARRGGBB — R 이 high byte, B 가 low byte.
    /// <c>ColorRef</c>(COLORREF) 의 0x00BBGGRR 과는 R/B 순서가 반대다.
    /// </para>
    /// </summary>
    public static bool TryGetColorizationRgb(out byte r, out byte g, out byte b)
    {
        int hr = DwmGetColorizationColor(out uint argb, out _);
        if (hr != 0)
        {
            r = g = b = 0;
            return false;
        }
        r = (byte)((argb >> 16) & 0xFF);
        g = (byte)((argb >> 8) & 0xFF);
        b = (byte)(argb & 0xFF);
        return true;
    }
}
