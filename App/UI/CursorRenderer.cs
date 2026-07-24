using KoEnVue.App.Config;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI;

/// <summary>
/// 커서 추종 인디케이터의 분석적 AA 픽셀 셰이더.
/// PR-29 이동 딤: <see cref="CursorStyle.MotionSoftness"/> &gt; 0 이면 하드 코어 없이
/// 가우시안 안개 띠로 Inner/Middle/Outer 를 그림 (CAPS OFF 면 Outer 생략).
/// </summary>
internal static class CursorRenderer
{
    private const double HalfPixel = 0.5;
    private const double MinVisibleAlpha = 1.0 / 255.0;

    private const double SubSampleLow = 0.25;
    private const double SubSampleHigh = 0.75;
    private const double InvSubSampleCount = 0.25;
    private const double EdgeMarginPx = 1.0;
    private const byte HaloWhiteComponent = 255;
    // 가우시안 안개: σ의 이 배수까지 샘플 (그 밖은 0 취급).
    private const double FogSigmaCutoff = 3.0;

    public static (int w, int h) Render(IntPtr ppvBits, CursorStyle style, CursorMetrics metrics)
    {
        int w = metrics.ScaledWidth;
        int h = metrics.ScaledHeight;
        if (w <= 0 || h <= 0) return (w, h);

        double scale = style.HighlightScale;
        double outerR = DpiHelper.Scale(style.OuterRadiusLogicalPx, metrics.DpiScale) * scale;
        double middleR = DpiHelper.Scale(style.MiddleRadiusLogicalPx, metrics.DpiScale) * scale;
        double innerR = DpiHelper.Scale(style.InnerRadiusLogicalPx, metrics.DpiScale) * scale;
        double baseCore = DpiHelper.Scale(style.CoreThicknessLogicalPx, metrics.DpiScale) * 0.5 * scale;
        double baseHalo = DpiHelper.Scale(style.HaloThicknessLogicalPx, metrics.DpiScale) * 0.5 * scale;
        double haloOpacity = Math.Clamp(style.HaloOpacity, 0.0, 1.0);

        double cx = w * 0.5;
        double cy = h * 0.5;

        double maxRing = style.CapsLockOn
            ? Math.Max(outerR, Math.Max(middleR, innerR))
            : Math.Max(middleR, innerR);

        double soft = Math.Clamp(style.MotionSoftness, 0.0, 1.0);
        double fogSigma = 0.0;
        if (soft > 0.0)
        {
            // 가우시안 안개 — 선형 AA 대신 σ 큰 감쇠. baseHalo(보통 1px) × 14 ≈ 14px σ.
            fogSigma = Math.Max(baseHalo, 0.5) * (1.0 + soft * (DefaultConfig.CursorMotionFogSigmaMul - 1.0));
        }

        double extent = soft > 0.0
            ? fogSigma * FogSigmaCutoff
            : Math.Max(baseCore, baseHalo) + HalfPixel;
        double maxOuterR = maxRing + extent + EdgeMarginPx;
        double maxOuterRSq = maxOuterR * maxOuterR;

        ShadeDib(ppvBits, w, h, cx, cy,
            outerR, middleR, innerR, baseCore, baseHalo, haloOpacity, soft, fogSigma, style.CapsLockOn,
            style.InnerColorArgb, style.MiddleColorArgb, style.OuterColorArgb,
            style.RingAlphaInner, style.RingAlphaMiddle, style.RingAlphaOuter,
            maxOuterRSq);

        return (w, h);
    }

    private static unsafe void ShadeDib(
        IntPtr ppvBits, int w, int h, double cx, double cy,
        double outerR, double middleR, double innerR,
        double coreHalf, double haloHalf, double haloOpacity,
        double soft, double fogSigma, bool capsOn,
        uint innerColorArgb, uint middleColorArgb, uint outerColorArgb,
        double ringAlphaInner, double ringAlphaMiddle, double ringAlphaOuter,
        double maxOuterRSq)
    {
        byte* ptr = (byte*)ppvBits;

        for (int y = 0; y < h; y++)
        {
            double dyTop = Math.Abs(y + SubSampleLow - cy);
            double dyBot = Math.Abs(y + SubSampleHigh - cy);
            double dyMin = Math.Min(dyTop, dyBot);
            if (dyMin * dyMin > maxOuterRSq)
            {
                ptr += w * DibSectionFactory.BytesPerPixel;
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

                        EvaluateRing(d, innerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, innerColorArgb, ringAlphaInner,
                            ref sampleAlpha, ref sampleR, ref sampleG, ref sampleB);
                        EvaluateRing(d, middleR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, middleColorArgb, ringAlphaMiddle,
                            ref sampleAlpha, ref sampleR, ref sampleG, ref sampleB);
                        if (capsOn)
                            EvaluateRing(d, outerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, outerColorArgb, ringAlphaOuter,
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
                    ptr[0] = (byte)Math.Round(accumB / accumA);
                    ptr[1] = (byte)Math.Round(accumG / accumA);
                    ptr[2] = (byte)Math.Round(accumR / accumA);
                    ptr[3] = (byte)Math.Round(avgAlpha * 255.0);
                }
                ptr += DibSectionFactory.BytesPerPixel;
            }
        }
    }

    private static void EvaluateRing(
        double d, double radius, double coreHalf, double haloHalf, double haloOpacity,
        double soft, double fogSigma,
        uint colorArgb, double ringAlphaScale,
        ref double bestAlpha, ref byte bestR, ref byte bestG, ref byte bestB)
    {
        if (ringAlphaScale <= 0.0) return;

        double dOffset = Math.Abs(d - radius);
        double ringAlpha;
        byte ringR, ringG, ringB;

        if (soft > 0.0 && fogSigma > 0.0)
        {
            // 가우시안 안개 — 하드 코어 없음. 색은 흰+흰색 혼합으로 탁하게.
            double t = dOffset / fogSigma;
            if (t >= FogSigmaCutoff) return;
            ringAlpha = Math.Exp(-0.5 * t * t) * ringAlphaScale;
            if (ringAlpha < MinVisibleAlpha) return;

            byte cr = (byte)((colorArgb >> 16) & 0xFF);
            byte cg = (byte)((colorArgb >> 8) & 0xFF);
            byte cb = (byte)(colorArgb & 0xFF);
            // 흰색 비중 ↑ (soft) → 더 안개답게
            double whiteness = DefaultConfig.CursorFogWhitenessBase
                + DefaultConfig.CursorFogWhitenessSoftSpan * soft;
            ringR = (byte)Math.Round(cr * (1.0 - whiteness) + HaloWhiteComponent * whiteness);
            ringG = (byte)Math.Round(cg * (1.0 - whiteness) + HaloWhiteComponent * whiteness);
            ringB = (byte)Math.Round(cb * (1.0 - whiteness) + HaloWhiteComponent * whiteness);
        }
        else
        {
            double coreCov = Clamp01(coreHalf + HalfPixel - dOffset);
            double haloFull = Clamp01(haloHalf + HalfPixel - dOffset);
            double haloOnly = Math.Max(0.0, haloFull - coreCov) * haloOpacity;
            ringAlpha = Math.Max(coreCov, haloOnly) * ringAlphaScale;
            if (ringAlpha <= 0.0) return;

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
