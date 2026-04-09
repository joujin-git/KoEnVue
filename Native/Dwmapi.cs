using System.Runtime.InteropServices;

namespace KoEnVue.Native;

internal static partial class Dwmapi
{
    /// <summary>
    /// DWM 윈도우 속성 조회. DWMWA_CAPTION_BUTTON_BOUNDS로 캡션 버튼 RECT 획득.
    /// </summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute,
        out RECT pvAttribute, uint cbAttribute);
}
