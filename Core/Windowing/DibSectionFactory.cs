using System.Runtime.InteropServices;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// top-down 32bpp BI_RGB DIB section 을 생성하고 지정된 memory DC 에 select 한다.
/// <para>
/// <see cref="LayeredOverlayBase"/> 와 <see cref="LayeredCursorBase"/> 두 렌더 엔진이
/// 동일 형식의 DIB section 생성을 중복 보유하던 것을 한 곳으로 합친 helper.
/// caller-side 상태 갱신 (실패 래치, ppvBits, _currentBitmap 교체, dimension 캐시,
/// _lastRenderedStyle 무효화) 은 호출자가 책임 — 본 helper 는 GDI 호출 한 단계만
/// 캡슐화한다. premultiply 후처리는 엔진별 의미 차이가 있어 공유하지 않는다.
/// </para>
/// </summary>
internal static class DibSectionFactory
{
    /// <summary>32bpp BI_RGB DIB section 의 픽셀당 바이트 수 (= <c>biBitCount</c> 32 / 8). 두 렌더
    /// 엔진의 픽셀 버퍼 클리어 / premultiply 루프가 공유하는 stride 단위.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// <paramref name="width"/> × <paramref name="height"/> 크기의 top-down 32bpp BI_RGB
    /// DIB section 을 생성한 뒤 <paramref name="memDC"/> 에 select 한다. 이전 비트맵의
    /// dispose 는 caller 책임.
    /// <para>
    /// 실패 시 <paramref name="bitmap"/>=null, <paramref name="ppvBits"/>=IntPtr.Zero,
    /// 반환값 false. caller 가 한 번만 Warning 로그하도록 실패 래치 정책 (예: 양 엔진의
    /// <c>_dibFailureLogged</c>) 은 caller 측에 유지한다.
    /// </para>
    /// </summary>
    public static bool TryCreate(
        IntPtr memDC,
        int width,
        int height,
        out SafeBitmapHandle? bitmap,
        out IntPtr ppvBits)
    {
        bitmap = null;
        ppvBits = IntPtr.Zero;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,  // top-down DIB
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32Constants.BI_RGB
        };

        IntPtr hBitmap = Gdi32.CreateDIBSection(
            IntPtr.Zero, ref bmi, Win32Constants.DIB_RGB_COLORS,
            out IntPtr bits, IntPtr.Zero, 0);

        if (hBitmap == IntPtr.Zero)
            return false;

        Gdi32.SelectObject(memDC, hBitmap);
        bitmap = new SafeBitmapHandle(hBitmap, true);
        ppvBits = bits;
        return true;
    }
}
