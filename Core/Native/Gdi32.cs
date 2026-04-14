using System.Runtime.InteropServices;

namespace KoEnVue.Core.Native;

internal static partial class Gdi32
{
    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut,
        uint iCharSet, uint iOutPrecision, uint iClipPrecision,
        uint iQuality, uint iPitchAndFamily, string pszFaceName);

    // 참고: CreateIconIndirect, FillRect는 user32.dll 소속 → User32.cs에 선언됨

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom,
        int width, int height);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(IntPtr hdc, uint color);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetTextExtentPoint32W(IntPtr hdc, string lpString, int c, out SIZE psizl);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW lptm);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr GetStockObject(int i);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreatePen(int iStyle, int cWidth, uint color);

    // 참고: DrawTextW는 user32.dll 소속 → User32.cs에 선언됨
    // 참고: MulDiv는 kernel32.dll 소속 → Kernel32.cs에 선언됨
}
