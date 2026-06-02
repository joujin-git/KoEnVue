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
    // avgAlpha × 255 가 round-down 으로 0 이 되는 픽셀은 셰이더 출력에서 제외. 미만이면 RGB 만 남고
    // alpha=0 인 부산 픽셀이 되어 LayeredCursorBase.ApplyPremultipliedAlpha 의 정리 분기에 의존하게 된다.
    private const double MinVisibleAlpha = 1.0 / 255.0;

    // 2×2 supersample 서브픽셀 오프셋 (저 0.25 / 고 0.75) + 4-샘플 평균 계수 (1/4).
    private const double SubSampleLow = 0.25;
    private const double SubSampleHigh = 0.75;
    private const double InvSubSampleCount = 0.25;
    // early-exit 반경의 AA 안전 여유 (1px).
    private const double EdgeMarginPx = 1.0;
    // 헤일로 = 흰색 (R=G=B 최대값).
    private const byte HaloWhiteComponent = 255;

    public static (int w, int h) Render(IntPtr ppvBits, CursorStyle style, CursorMetrics metrics)
    {
        int w = metrics.ScaledWidth;
        int h = metrics.ScaledHeight;
        if (w <= 0 || h <= 0) return (w, h);

        // IME 전환 스케일 팝 — 동심원 전체(반지름 + 두께)에 현재 배율을 곱해 통째로 확대.
        // 평상시 HighlightScale=1.0 → 무변화. 팝 중 1.0~CursorHighlightScale 사이 보간값이 들어온다.
        double scale = style.HighlightScale;
        double outerR = DpiHelper.Scale(style.OuterRadiusLogicalPx, metrics.DpiScale) * scale;
        double middleR = DpiHelper.Scale(style.MiddleRadiusLogicalPx, metrics.DpiScale) * scale;
        double innerR = DpiHelper.Scale(style.InnerRadiusLogicalPx, metrics.DpiScale) * scale;
        double coreHalf = DpiHelper.Scale(style.CoreThicknessLogicalPx, metrics.DpiScale) * 0.5 * scale;
        double haloHalf = DpiHelper.Scale(style.HaloThicknessLogicalPx, metrics.DpiScale) * 0.5 * scale;
        double haloOpacity = Math.Clamp(style.HaloOpacity, 0.0, 1.0);

        double cx = w * 0.5;
        double cy = h * 0.5;

        // Early exit 반경 — 가장 먼 가능 거리 = max(visible radii) + max(coreHalf, haloHalf) + 1.
        double maxRing = style.CapsLockOn
            ? Math.Max(outerR, Math.Max(middleR, innerR))
            : Math.Max(middleR, innerR);
        double maxOuterR = maxRing + Math.Max(coreHalf, haloHalf) + EdgeMarginPx;
        double maxOuterRSq = maxOuterR * maxOuterR;

        ShadeDib(ppvBits, w, h, cx, cy,
            outerR, middleR, innerR, coreHalf, haloHalf, haloOpacity, style.CapsLockOn,
            style.InnerColorArgb, style.MiddleColorArgb, style.OuterColorArgb,
            maxOuterRSq);

        return (w, h);
    }

    /// <summary>
    /// 2x2 supersampling — 픽셀당 4 sub-sample (0.25 / 0.75 오프셋) 평가 후 alpha-weighted 색상
    /// 평균 + alpha 평균. 1x sample 의 box-filter AA 보다 가장자리 4배 부드러움. 비용도 ~4배지만
    /// cursor 인디 DIB 가 작고 (디폴트 96x96, early exit 후 실효 ~30%), Render 가 매 motion tick 마다
    /// 아닌 cursor 정지 시점에 한 번 그리고 픽셀 캐시되므로 폴링 50ms 안에서 무시 가능.
    /// </summary>
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
            // row early exit — 2 sub-y (0.25, 0.75) 중 가장 가까운 거리 기준
            double dyTop = Math.Abs(y + SubSampleLow - cy);
            double dyBot = Math.Abs(y + SubSampleHigh - cy);
            double dyMin = Math.Min(dyTop, dyBot);
            if (dyMin * dyMin > maxOuterRSq)
            {
                ptr += w * BytesPerPixel;
                continue;
            }

            for (int x = 0; x < w; x++)
            {
                double accumA = 0.0;
                double accumR = 0.0, accumG = 0.0, accumB = 0.0;

                for (int sy = 0; sy < 2; sy++)
                {
                    double subY = y + (sy == 0 ? SubSampleLow : SubSampleHigh);
                    double dy = subY - cy;
                    double dy2 = dy * dy;
                    if (dy2 > maxOuterRSq) continue;

                    for (int sx = 0; sx < 2; sx++)
                    {
                        double subX = x + (sx == 0 ? SubSampleLow : SubSampleHigh);
                        double dx = subX - cx;
                        double distSq = dx * dx + dy2;
                        if (distSq > maxOuterRSq) continue;

                        double d = Math.Sqrt(distSq);

                        double sampleAlpha = 0.0;
                        byte sampleR = 0, sampleG = 0, sampleB = 0;

                        EvaluateRing(d, innerR, coreHalf, haloHalf, haloOpacity, innerColorArgb,
                            ref sampleAlpha, ref sampleR, ref sampleG, ref sampleB);
                        EvaluateRing(d, middleR, coreHalf, haloHalf, haloOpacity, middleColorArgb,
                            ref sampleAlpha, ref sampleR, ref sampleG, ref sampleB);
                        if (capsOn)
                            EvaluateRing(d, outerR, coreHalf, haloHalf, haloOpacity, outerColorArgb,
                                ref sampleAlpha, ref sampleR, ref sampleG, ref sampleB);

                        if (sampleAlpha > 0.0)
                        {
                            accumA += sampleAlpha;
                            accumR += sampleAlpha * sampleR;
                            accumG += sampleAlpha * sampleG;
                            accumB += sampleAlpha * sampleB;
                        }
                    }
                }

                double avgAlpha = accumA * InvSubSampleCount;
                if (avgAlpha >= MinVisibleAlpha)
                {
                    // alpha-weighted 색상 평균 (sub-sample alpha 합으로 정규화)
                    ptr[0] = (byte)Math.Round(accumB / accumA);
                    ptr[1] = (byte)Math.Round(accumG / accumA);
                    ptr[2] = (byte)Math.Round(accumR / accumA);
                    ptr[3] = (byte)Math.Round(avgAlpha * 255.0);
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
            ringR = HaloWhiteComponent;
            ringG = HaloWhiteComponent;
            ringB = HaloWhiteComponent;
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
