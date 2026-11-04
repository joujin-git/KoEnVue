using System.Runtime.InteropServices;
using KoEnVue.App.UI;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 커서 헤일로 안개 거리표(LUT) 경로가 샘플마다 직접 계산하는 정밀 경로와 같은 그림을 내는지.
/// 2026-11-04 실측: 알파 차이 ≤ 1/255, 색 차이 0 (CapsLock 색 섞기 도입 전엔 바깥 원 교차선에서 보이는 픽셀
/// 0.2% 이하·알파 곱 ≤ 8/255 의 색 차이가 있었다 — 섞기 후 교차선의 색 급변이 사라져 표 보간과 일치).
/// σ ≥ 8px 이면 거리표 경로는 픽셀 중심 1점만 샘플한다(정밀 경로는 2×2). 그 케이스는 알파 허용치가 2 다 — 3σ 에서 잘리는
/// 안개 끝(≈1.5/255 계단)에 걸친 픽셀을 2×2 는 1/255 미만으로 평균해 0, 1점은 2 로 칠한다(2026-11-04 실측, 원 알파
/// 0.55·1.0 모두 ≤2). CapsLock ON 이면 문턱은 좁힌 바깥 원 σ(CapsFogSigma) 기준이다.
/// </summary>
public class CursorRendererFogLutTests
{
    private const uint Blue = 0xFF1A73E8;
    private const uint Orange = 0xFFD97706;

    // 알파 허용치: 1점 샘플 경로(σ ≥ 8px) 2, 2×2 거리표 경로(σ < 8px) 1 — 경로별 회귀 감도 유지.
    private const int OnePointAlphaTolerance = 2;
    private const int SupersampleAlphaTolerance = 1;

    public static TheoryData<int, int, int, int, double, double, bool, int> Cases => new()
    {
        // outer, middle, inner, haloThickness, softness, dpi, capsLock, 알파 허용치
        { 96, 20, 10, 12, 0.8, 1.0, false, OnePointAlphaTolerance },
        { 96, 20, 10, 12, 0.8, 1.5, true, OnePointAlphaTolerance },
        { 45, 35, 30, 2, 0.35, 1.25, true, SupersampleAlphaTolerance }, // σ≈7.2px (halo 2×1.25 → 반올림 2)
        { 45, 35, 30, 0, 0.01, 1.0, false, SupersampleAlphaTolerance }, // 최소 σ(≈0.57px) — 보간 오차가 가장 큰 쪽
        // 작은 반지름 + 1점 샘플(σ≈9.1px) + 홀수 변(115px): 중심 꼭짓점 픽셀 — 중심 2×2 보정 없으면 실패.
        // CapsLock OFF 여야 한다 — ON 이면 1점 문턱을 좁힌 바깥 σ(4px)로 판정해 2×2 경로로 가 이 보정을 시험하지 못한다.
        { 8, 6, 4, 1, 1.0, 1.25, false, OnePointAlphaTolerance },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void FogLut_MatchesExactPath(
        int outer, int middle, int inner, int halo, double soft, double dpi, bool caps, int alphaTolerance)
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
        Assert.InRange(maxAlphaDiff, 0, alphaTolerance);
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

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.5, true)]
    public void ClearZone_FogIsZeroNearCursorAndUnchangedOutside(double dpi, bool caps)
    {
        // 2026-11-04 사용자 요청: 커서 주변 글자가 안개로 뿌옇게 보이지 않도록 비움 반지름 안쪽은 안개 0.
        var baseStyle = new CursorStyle(96, 20, 10, 1, 12, 0.5, Blue, Blue, Orange, caps,
            HighlightScale: 1.3, MotionSoftness: 0.8, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.53, RingAlphaOuter: 0.52);
        const int clearLogical = 24;
        var cleared = baseStyle with { ClearRadiusLogicalPx = clearLogical };
        int side = (int)Math.Ceiling(baseStyle.BoundingBoxLogicalPx * dpi);
        byte[] before = RenderToBytes(baseStyle, dpi, useFogLut: true);
        byte[] after = RenderToBytes(cleared, dpi, useFogLut: true);
        byte[] afterExact = RenderToBytes(cleared, dpi, useFogLut: false);

        double clearR = Math.Round(clearLogical * dpi);
        double clearEnd = clearR + Math.Round(KoEnVue.App.Config.DefaultConfig.CursorClearFadeWidth * dpi);
        int insideVisible = 0, outsideChanged = 0, maxLutDiff = 0;
        bool beforeHadFogInside = false;
        for (int y = 0; y < side; y++)
        for (int x = 0; x < side; x++)
        {
            int i = (y * side + x) * 4 + 3;
            double dx = x + 0.5 - side * 0.5, dy = y + 0.5 - side * 0.5;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < clearR - 1.0)
            {
                if (after[i] > 0) insideVisible++;
                if (before[i] > 0) beforeHadFogInside = true;
            }
            else if (d > clearEnd + 1.0 && before[i] != after[i]) outsideChanged++;
            maxLutDiff = Math.Max(maxLutDiff, Math.Abs(after[i] - afterExact[i]));
        }

        Assert.True(beforeHadFogInside, "비움 없이는 커서 주변에 안개가 있어야 함 (대조군)");
        Assert.Equal(0, insideVisible);
        Assert.Equal(0, outsideChanged);
        Assert.InRange(maxLutDiff, 0, OnePointAlphaTolerance);
    }

    [Theory]
    [InlineData(Blue)]
    [InlineData(Orange)]
    public void CapsFog_LanguageInsideCapsBandOutside(uint languageColor)
    {
        // 2026-11-04 사용자 요청: CapsLock 은 한/영 헤일로를 덮지 말고 그 바깥에 전용 색(보라) 띠로.
        // 바깥 원 σ 를 좁히기 전에는 넓은 CapsLock 안개가 한/영 안개를 통째로 덮어 언어 색이 안 보였다.
        const uint purple = 0xFF9333EA;
        var style = new CursorStyle(96, 20, 10, 1, 12, 0.5, languageColor, languageColor, purple, true,
            MotionSoftness: 0.8, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.55 * 0.97, RingAlphaOuter: 0.55 * 0.94, ClearRadiusLogicalPx: 48);
        int side = style.BoundingBoxLogicalPx;
        byte[] px = RenderToBytes(style, 1.0, useFogLut: true);
        double white = KoEnVue.App.Config.DefaultConfig.CursorFogWhitenessBase
            + KoEnVue.App.Config.DefaultConfig.CursorFogWhitenessSoftSpan * 0.8;

        int mid = side / 2;
        Assert.True(CloserTo(px, side, mid + 70, mid, languageColor, purple, white), "비움 바로 바깥은 언어 색이어야 함");
        Assert.True(CloserTo(px, side, mid + 96, mid, purple, languageColor, white), "바깥 원은 CapsLock 색이어야 함");
    }

    private static bool CloserTo(byte[] px, int side, int x, int y, uint want, uint other, double white)
    {
        int i = (y * side + x) * 4;
        double dWant = 0, dOther = 0;
        for (int c = 0; c < 3; c++)
        {
            int shift = 8 * c; // BGRA 바이트 c ↔ ARGB 의 B(0)/G(8)/R(16)
            double w = ((want >> shift) & 0xFF) * (1 - white) + 255 * white;
            double o = ((other >> shift) & 0xFF) * (1 - white) + 255 * white;
            dWant += Math.Abs(px[i + c] - w);
            dOther += Math.Abs(px[i + c] - o);
        }
        return dWant < dOther;
    }

    [Theory]
    [InlineData(45, 35, 30, 2, 0.35)] // v1.0.2.0 기본값 — 업그레이드 사용자 설정 파일에 그대로 남아 있다
    [InlineData(96, 20, 10, 2, 0.8)]  // 현재 원 + 얇은 헤일로
    public void ClearZone_NeverErasesWholeFog(int outer, int middle, int inner, int halo, double soft)
    {
        // release-review 2026-11-04: 고정 크기 비움(기본 48px)이 작은 원·좁은 안개를 통째로 지워 헤일로가
        // 사라졌다(최대 알파 0.4/255). 비움 끝을 한/영 원 + 1.5σ 로 제한한 뒤에는 안개 몸통이 남아야 한다.
        var style = new CursorStyle(outer, middle, inner, 1, halo, 0.5, Blue, Blue, Orange, false,
            MotionSoftness: soft, MotionFogPadLogicalPx: 28,
            RingAlphaInner: 0.55, RingAlphaMiddle: 0.53, RingAlphaOuter: 0.52);
        int maxNoClear = MaxAlpha(RenderToBytes(style, 1.0, useFogLut: true));
        int maxCleared = MaxAlpha(RenderToBytes(style with { ClearRadiusLogicalPx = 48 }, 1.0, useFogLut: true));

        Assert.True(maxNoClear > 64, "대조군: 비움 없이는 안개가 보여야 함");
        // 기준: 비움 없을 때의 1/3 이상 + 눈에 보이는 32/255 이상. 한도를 더 풀면 커서 옆 글자가 다시 뿌옇게 되는
        // 맞교환이라 「헤일로가 남는다」만 고정한다 (실측 2026-11-04: 얇은 헤일로 59/140, v1.0.2.0 기본값은 더 남음).
        Assert.True(maxCleared * 3 >= maxNoClear && maxCleared >= 32, $"비움이 안개를 거의 다 지움: {maxCleared} vs {maxNoClear}");
    }

    private static int MaxAlpha(byte[] px)
    {
        int max = 0;
        for (int i = 3; i < px.Length; i += 4) max = Math.Max(max, px[i]);
        return max;
    }

    [Fact]
    public void ClearZone_DoesNotAffectSharpMode()
    {
        // 선명하게(soft=0)는 가는 고리라 안쪽이 이미 비어 있다 — 비움 설정이 고리를 지우면 안 된다.
        var style = new CursorStyle(45, 35, 10, 2, 4, 0.5, Blue, Blue, Orange, true);
        byte[] off = RenderToBytes(style, 1.0, useFogLut: true);
        byte[] on = RenderToBytes(style with { ClearRadiusLogicalPx = 40 }, 1.0, useFogLut: true);
        Assert.Equal(off, on);
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
