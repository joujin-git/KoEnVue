using System.Runtime.InteropServices;
using KoEnVue.App.UI;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 커서 헤일로 안개 거리표(LUT) 경로가 샘플마다 직접 계산하는 정밀 경로와 같은 그림을 내는지.
/// 2026-11-04 도입 시 실측: 알파 차이 ≤ 1/255, 색 차이는 CapsLock 바깥 원이 안쪽 원과 겹치는 경계선
/// 위에서만(보이는 픽셀의 0.2% 이하, 알파 곱 기준 ≤ 8/255).
/// </summary>
public class CursorRendererFogLutTests
{
    private const uint Blue = 0xFF1A73E8;
    private const uint Orange = 0xFFD97706;

    public static TheoryData<int, int, int, int, double, double, bool> Cases => new()
    {
        // outer, middle, inner, haloThickness, softness, dpi, capsLock
        { 96, 20, 10, 12, 0.8, 1.0, false },
        { 96, 20, 10, 12, 0.8, 1.5, true },
        { 45, 35, 30, 2, 0.35, 1.25, true },
        { 45, 35, 30, 0, 0.01, 1.0, false }, // 최소 σ(≈0.57px) — 보간 오차가 가장 큰 쪽
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FogLut_MatchesExactPath(
        int outer, int middle, int inner, int halo, double soft, double dpi, bool caps)
    {
        var style = new CursorStyle(outer, middle, inner, 1, halo, 0.5, Blue, Blue, Orange, caps,
            HighlightScale: 1.3, MotionSoftness: soft, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.53, RingAlphaOuter: 0.52);

        byte[] exact = RenderToBytes(style, dpi, useFogLut: false);
        byte[] lut = RenderToBytes(style, dpi, useFogLut: true);

        int visible = 0, colorOff = 0, maxAlphaDiff = 0, maxPremulDiff = 0;
        for (int i = 0; i < exact.Length; i += 4)
        {
            int ae = exact[i + 3], al = lut[i + 3];
            if (ae > 0) visible++;
            maxAlphaDiff = Math.Max(maxAlphaDiff, Math.Abs(ae - al));
            int pixelPremul = 0;
            for (int c = 0; c < 3; c++)
                pixelPremul = Math.Max(pixelPremul, Math.Abs(exact[i + c] * ae - lut[i + c] * al) / 255);
            maxPremulDiff = Math.Max(maxPremulDiff, pixelPremul);
            if (pixelPremul > 2) colorOff++;
        }

        Assert.True(visible > 0);
        Assert.InRange(maxAlphaDiff, 0, 1);
        Assert.InRange(maxPremulDiff, 0, 8);
        Assert.True(colorOff * 100 <= visible, $"색 차이 픽셀 {colorOff}/{visible} > 1%");
    }

    [Fact]
    public void Output_IsMirrorSymmetric()
    {
        var style = new CursorStyle(96, 20, 10, 1, 12, 0.5, Blue, Blue, Orange, true,
            HighlightScale: 1.3, MotionSoftness: 0.8, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.53, RingAlphaOuter: 0.52);
        const double dpi = 1.25; // 홀수 변(585px) — 가운데 줄이 자기 거울인 경우
        int side = (int)Math.Ceiling(style.BoundingBoxLogicalPx * dpi);
        byte[] px = RenderToBytes(style, dpi, useFogLut: true);

        Assert.Equal(1, side % 2);

        // 대칭 검사만으로는 4분면 범위가 줄어 가운데 줄이 통째로 빈 경우(0 도 자기 거울)를 못 잡는다.
        // 원은 가운데 행·열과 반드시 교차하므로 그 줄에 보이는 픽셀이 있어야 한다.
        int mid = side / 2;
        bool midRowVisible = false, midColVisible = false;
        for (int i = 0; i < side; i++)
        {
            midRowVisible |= px[(mid * side + i) * 4 + 3] > 0;
            midColVisible |= px[(i * side + mid) * 4 + 3] > 0;
        }
        Assert.True(midRowVisible, "가운데 행이 비어 있음");
        Assert.True(midColVisible, "가운데 열이 비어 있음");

        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int a = (y * side + x) * 4;
            int m = (y * side + (side - 1 - x)) * 4;
            int v = ((side - 1 - y) * side + x) * 4;
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal(px[a + c], px[m + c]);
                Assert.Equal(px[a + c], px[v + c]);
            }
        }
    }

    private static byte[] RenderToBytes(CursorStyle style, double dpi, bool useFogLut)
    {
        int side = (int)Math.Ceiling(style.BoundingBoxLogicalPx * dpi);
        int bytes = side * side * 4;
        IntPtr buf = Marshal.AllocHGlobal(bytes);
        try
        {
            Marshal.Copy(new byte[bytes], 0, buf, bytes); // DIB 는 0 으로 비운 뒤 셰이딩된다
            CursorRenderer.RenderCore(buf, style, new CursorMetrics(dpi, side, side), useFogLut);
            byte[] result = new byte[bytes];
            Marshal.Copy(buf, result, 0, bytes);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}
