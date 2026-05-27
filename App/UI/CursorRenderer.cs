using KoEnVue.Core.Dpi;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI;

/// <summary>
/// 커서 추종 인디케이터의 분석적 AA 픽셀 셰이더. <see cref="LayeredCursorBase"/> 의
/// <c>renderToDib</c> 콜백으로 주입되어 closed type <see cref="CursorStyle"/> 와
/// <see cref="CursorMetrics"/> 를 받아 DIB 의 BGRA32 픽셀에 직접 쓴다.
/// <para>
/// 모델 — 각 원 (inner / middle / outer) 은 두께 <c>coreT + 2 × (haloT - coreT) / 2 = haloT</c> 의
/// 동심 링. 픽셀의 원 중심선까지 거리 <c>d_offset = |d - radius|</c> 가
/// </para>
/// <list type="bullet">
///   <item><c>≤ coreT/2</c> → 코어 색상 alpha 1.0 (양옆 0.5px AA)</item>
///   <item><c>≤ haloT/2</c> → 헤일로 색상 (흰색 × HaloOpacity, 코어 영역 제외)</item>
/// </list>
/// <para>
/// 사용자 설명 "코어 2 px 양옆으로 흰 헤일로가 0.5 px씩 비침 → 총 시각 두께 3 px" 와 정확히 일치:
/// 헤일로 (3px) 가 코어 (2px) 보다 양옆 0.5px 씩 외부로 확장.
/// </para>
/// <para>
/// CAPS LOCK OFF 시 외측 원 skip — distance 계산 자체를 건너뜀. DIB bbox 는 항상 외측 반지름 기준
/// 이라 CAPS 토글 시 DIB 재생성 없이 같은 bbox 안에서 픽셀만 재계산.
/// </para>
/// </summary>
internal static class CursorRenderer
{
    private const int BytesPerPixel = 4;
    private const double HalfPixel = 0.5;

    public static (int w, int h) Render(IntPtr ppvBits, CursorStyle style, CursorMetrics metrics)
    {
        int w = metrics.ScaledWidth;
        int h = metrics.ScaledHeight;
        if (w <= 0 || h <= 0) return (w, h);

        double outerR = DpiHelper.Scale(style.OuterRadiusLogicalPx, metrics.DpiScale);
        double middleR = DpiHelper.Scale(style.MiddleRadiusLogicalPx, metrics.DpiScale);
        double innerR = DpiHelper.Scale(style.InnerRadiusLogicalPx, metrics.DpiScale);
        double coreHalf = DpiHelper.Scale(style.CoreThicknessLogicalPx, metrics.DpiScale) * 0.5;
        double haloHalf = DpiHelper.Scale(style.HaloThicknessLogicalPx, metrics.DpiScale) * 0.5;
        double haloOpacity = Math.Clamp(style.HaloOpacity, 0.0, 1.0);

        double cx = w * 0.5;
        double cy = h * 0.5;

        // Early exit 반경 — 가장 먼 가능 거리 = max(visible radii) + max(coreHalf, haloHalf) + 1.
        double maxRing = style.CapsLockOn
            ? Math.Max(outerR, Math.Max(middleR, innerR))
            : Math.Max(middleR, innerR);
        double maxOuterR = maxRing + Math.Max(coreHalf, haloHalf) + 1.0;
        double maxOuterRSq = maxOuterR * maxOuterR;

        ShadeDib(ppvBits, w, h, cx, cy,
            outerR, middleR, innerR, coreHalf, haloHalf, haloOpacity, style.CapsLockOn,
            style.InnerColorArgb, style.MiddleColorArgb, style.OuterColorArgb,
            maxOuterRSq);

        return (w, h);
    }

    private static unsafe void ShadeDib(
        IntPtr ppvBits, int w, int h, double cx, double cy,
        double outerR, double middleR, double innerR,
        double coreHalf, double haloHalf, double haloOpacity, bool capsOn,
        uint innerColorArgb, uint middleColorArgb, uint outerColorArgb,
        double maxOuterRSq)
    {
        byte* ptr = (byte*)ppvBits;

        for (int y = 0; y < h; y++)
        {
            double dy = y + HalfPixel - cy;
            double dy2 = dy * dy;

            if (dy2 > maxOuterRSq)
            {
                ptr += w * BytesPerPixel;
                continue;
            }

            for (int x = 0; x < w; x++)
            {
                double dx = x + HalfPixel - cx;
                double distSq = dx * dx + dy2;

                if (distSq > maxOuterRSq)
                {
                    ptr += BytesPerPixel;
                    continue;
                }

                double d = Math.Sqrt(distSq);

                double bestAlpha = 0.0;
                byte bestR = 0, bestG = 0, bestB = 0;

                EvaluateRing(d, innerR, coreHalf, haloHalf, haloOpacity, innerColorArgb,
                    ref bestAlpha, ref bestR, ref bestG, ref bestB);
                EvaluateRing(d, middleR, coreHalf, haloHalf, haloOpacity, middleColorArgb,
                    ref bestAlpha, ref bestR, ref bestG, ref bestB);
                if (capsOn)
                    EvaluateRing(d, outerR, coreHalf, haloHalf, haloOpacity, outerColorArgb,
                        ref bestAlpha, ref bestR, ref bestG, ref bestB);

                if (bestAlpha > 0.0)
                {
                    ptr[0] = bestB;
                    ptr[1] = bestG;
                    ptr[2] = bestR;
                    ptr[3] = (byte)Math.Round(bestAlpha * 255.0);
                }

                ptr += BytesPerPixel;
            }
        }
    }

    /// <summary>
    /// 한 동심원에 대한 거리장 셰이딩. 코어 winner 면 사용자 색상, 헤일로 winner 면 흰색.
    /// ring_alpha 가 기존 best 보다 크면 best 갱신 — 여러 원이 겹쳐도 가장 강한 ring 채택.
    /// </summary>
    private static void EvaluateRing(
        double d, double radius, double coreHalf, double haloHalf, double haloOpacity,
        uint colorArgb,
        ref double bestAlpha, ref byte bestR, ref byte bestG, ref byte bestB)
    {
        double dOffset = Math.Abs(d - radius);

        double coreCov = Clamp01(coreHalf + HalfPixel - dOffset);
        double haloFull = Clamp01(haloHalf + HalfPixel - dOffset);
        double haloOnly = Math.Max(0.0, haloFull - coreCov) * haloOpacity;

        double ringAlpha = Math.Max(coreCov, haloOnly);
        if (ringAlpha <= 0.0) return;

        byte ringR, ringG, ringB;
        if (coreCov >= haloOnly)
        {
            ringR = (byte)((colorArgb >> 16) & 0xFF);
            ringG = (byte)((colorArgb >> 8) & 0xFF);
            ringB = (byte)(colorArgb & 0xFF);
        }
        else
        {
            ringR = 255;
            ringG = 255;
            ringB = 255;
        }

        if (ringAlpha > bestAlpha)
        {
            bestAlpha = ringAlpha;
            bestR = ringR;
            bestG = ringG;
            bestB = ringB;
        }
    }

    private static double Clamp01(double v)
    {
        if (v <= 0.0) return 0.0;
        if (v >= 1.0) return 1.0;
        return v;
    }
}
