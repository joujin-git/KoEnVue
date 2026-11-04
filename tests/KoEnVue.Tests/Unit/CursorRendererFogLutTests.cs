using System.Runtime.InteropServices;
using KoEnVue.App.UI;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 커서 헤일로 안개 거리표(LUT) 경로가 샘플마다 직접 계산하는 정밀 경로와 같은 그림을 내는지.
/// 2026-11-04 실측: 알파 차이 ≤ 1/255, 색 차이 0 (CapsLock 색 섞기 도입 전엔 바깥 원 교차선에서 보이는 픽셀
/// 0.2% 이하·알파 곱 ≤ 8/255 의 색 차이가 있었다 — 섞기 후 교차선의 색 급변이 사라져 표 보간과 일치).
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
        Assert.InRange(maxPremulDiff, 0, 2);
        Assert.Equal(0, colorOff);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void WideFog_FadesToZeroAtDibEdge(double dpi)
    {
        // release-review 2026-11-04: 기본값(halo 12·soft 0.8) + CapsLock + 전환 팝(×1.3) 에서 안개 3σ 가
        // DIB 를 넘어 변에서 직선으로 잘렸다(가장자리 알파 ≈62/255). 경계 흐림 후 테두리 줄은 완전히 투명해야 한다.
        var style = new CursorStyle(96, 20, 10, 1, 12, 0.5, Blue, Blue, Orange, true,
            HighlightScale: 1.3, MotionSoftness: 0.8, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.53, RingAlphaOuter: 0.52);
        int side = (int)Math.Ceiling(style.BoundingBoxLogicalPx * dpi);
        byte[] px = RenderToBytes(style, dpi, useFogLut: true);

        int maxBorderAlpha = 0;
        for (int i = 0; i < side; i++)
        {
            maxBorderAlpha = Math.Max(maxBorderAlpha, px[i * 4 + 3]);                          // 윗변
            maxBorderAlpha = Math.Max(maxBorderAlpha, px[((side - 1) * side + i) * 4 + 3]);    // 아랫변
            maxBorderAlpha = Math.Max(maxBorderAlpha, px[(i * side) * 4 + 3]);                 // 왼변
            maxBorderAlpha = Math.Max(maxBorderAlpha, px[(i * side + side - 1) * 4 + 3]);      // 오른변
        }
        Assert.Equal(0, maxBorderAlpha);

        // 흐림이 안개를 통째로 지우지는 않는다 — 바깥 원(반지름 96×1.3) 근처는 여전히 보인다.
        int mid = side / 2;
        int ringX = mid + (int)Math.Round(96 * 1.3 * dpi);
        Assert.True(px[(mid * side + ringX) * 4 + 3] > 64, "바깥 원 근처 안개가 사라짐");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.3)]
    public void CapsFog_ColorChangesSmoothlyAcrossRings(double scale)
    {
        // 2026-11-04 사용자 보고: 안개 모드 CapsLock 에서 「가장 진한 원의 색」만 칠해 안쪽(현재 색)↔바깥(반대 색)
        // 알파가 같아지는 반지름에서 색이 뚝 바뀌는 원형 경계가 보였다. 중심선을 따라 이웃 픽셀 간 색 변화가 작아야 한다.
        var style = new CursorStyle(96, 20, 10, 1, 12, 0.5, Orange, Orange, Blue, true,
            HighlightScale: scale, MotionSoftness: 0.8, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.55 * 0.97, RingAlphaOuter: 0.55 * 0.94);
        int side = style.BoundingBoxLogicalPx;
        byte[] px = RenderToBytes(style, 1.0, useFogLut: true);

        int mid = side / 2;
        int maxStep = 0;
        for (int x = mid; x < side - 1; x++)
        {
            int a = (mid * side + x) * 4, n = a + 4;
            if (px[a + 3] < 32 || px[n + 3] < 32) continue; // 안개 끝(거의 투명)은 제외
            for (int c = 0; c < 3; c++)
                maxStep = Math.Max(maxStep, Math.Abs(px[a + c] - px[n + c]));
        }
        Assert.InRange(maxStep, 0, 4);
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
