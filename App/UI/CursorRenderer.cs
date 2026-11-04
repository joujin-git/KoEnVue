using KoEnVue.App.Config;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI;

/// <summary>
/// 커서 헤일로의 분석적 AA 픽셀 셰이더.
/// PR-29 이동 딤: <see cref="CursorStyle.MotionSoftness"/> &gt; 0 이면 하드 코어 없이
/// 가우시안 안개 띠로 Inner/Middle/Outer 를 그림 (CAPS OFF 면 Outer 생략).
/// </summary>
internal static class CursorRenderer
{
    private const double HalfPixel = 0.5;
    private const double MinVisibleAlpha = 1.0 / 255.0;
    // 단위구간 [0,1] — DefaultConfig.MinOpacity(0.1) 과 의미 다름 (P3).
    private const double AlphaUnitMin = 0.0;
    private const double AlphaUnitMax = 1.0;
    private const double ByteChannelMax = (double)byte.MaxValue;

    private const double SubSampleLow = 0.25;
    private const double SubSampleHigh = 0.75;
    private const double InvSubSampleCount = 0.25;
    private const double EdgeMarginPx = 1.0;
    private const byte HaloWhiteComponent = 255;
    // 가우시안 안개: σ의 이 배수까지 샘플 (그 밖은 0 취급).
    private const double FogSigmaCutoff = 3.0;

    // 안개 거리표(LUT) 해상도 — 1px 당 칸 수. 안개는 중심 거리 d 만의 함수라 d 축 1차원 표로 충분하다.
    // 칸 사이 알파는 선형 보간: 최소 σ(≈0.57px) 에서도 보간 오차 < 1/255.
    private const int FogLutStepsPerPx = 16;
    // 표 끝 보간용 여유 칸 (i+1 접근).
    private const int FogLutTailEntries = 2;
    // 칸 사이 위치가 이 값 미만이면 앞 칸이 가깝다 (색 선택용).
    private const double FogLutNearestSplit = 0.5;
    // 안개 CapsLock 색 섞기 날카로움 — 원별 알파의 이 거듭제곱으로 가중. 1 이면 넓게 섞여 보색이 회색이 되고,
    // 클수록 경계가 좁아진다(너무 크면 다시 뚝 끊김). 미리보기 비교로 정함.
    private const double FogCapsColorSharpness = 4.0;
    // 안개 CapsLock 바깥 원의 σ = 안개 σ × 이 비율 (σ 가 CapsFogMinSigmaPx 보다 크면, 0.35/0.25 렌더 비교로 정함). 같은 σ 로 그리면
    // 넓은 안개가 한/영 안개를 통째로 덮어 CapsLock 만 보였다 — 좁혀서 한/영 안개 바깥에 띠로 두른다
    // (2026-11-04 사용자 요청: 「한/영 헤일로 바깥에 CapsLock 보라」).
    private const double CapsFogSigmaRatio = 0.25;
    private const double CapsFogMinSigmaPx = 4.0;
    // DIB 경계 흐림 폭의 하한 (px) — 흐림 폭은 보통 σ.
    private const double EdgeFadeMinWidthPx = 1.0;
    // 안개 σ 가 이 값(px) 이상이면 픽셀 중심 1점만 샘플(2×2 대신) — 300% DPI 팝 프레임이 15ms 주기를 넘던 것을 줄인다.
    // 넓은 안개는 한 픽셀 안에서 거의 변하지 않는다. 2×2 정밀 경로와 차이(2026-11-04 실측)는 알파 ≤2/255 로, 두 원이 만나는
    // 접힌 선(σ 에 반비례 — σ 4.9px 에서 3/255 라 8px 로 정함)과 3σ 안개 끝의 원래 계단(≈1.5/255)에서만 생긴다.
    // CapsLock ON 이면 좁힌 바깥 원 σ(CapsFogSigma) 로 판정한다 — 가장 좁은 안개가 기준.
    private const double SingleSampleMinFogSigmaPx = 8.0;
    // 1점 샘플 경로에서도 중심에서 이 거리(px) 안쪽 픽셀은 2×2 로 — 거리 함수가 중심에서 뾰족해(원뿔 꼭짓점)
    // 반지름이 작은 설정(σ≈9px)에서 중앙 픽셀만 4~5/255 어긋났다.
    private const double CenterSupersampleRadiusPx = 1.0;

    /// <summary>
    /// 중심 거리(px)에 따른 안개 알파 배수 — 두 구간의 smoothstep 곱.
    /// <para>커서 비움 [ClearStart, ClearEnd]: 안쪽은 0, ClearEnd 부터 1 — 커서 바로 주변 글자가 안개로 뿌옇게 보이지 않게
    /// (<see cref="CursorStyle.ClearRadiusLogicalPx"/>, 2026-11-04 사용자 요청). ClearEnd ≤ ClearStart 면 비움 없음.</para>
    /// <para>DIB 경계 흐림 [EdgeStart, EdgeEnd]: 넓은 안개(3σ)가 정사각 DIB 를 넘으면 변에서 직선으로
    /// 잘리므로(release-review 2026-11-04 — 전환 팝 + CapsLock 에서 경계 알파 ≈62/255), 내접원 EdgeEnd 까지
    /// 0 이 되게 감쇠해 둥글고 부드러운 끝을 만든다. EdgeEnd ≤ EdgeStart 면 감쇠 없음.</para>
    /// </summary>
    private readonly record struct RadialFade(double ClearStart, double ClearEnd, double EdgeStart, double EdgeEnd)
    {
        public double Factor(double d)
        {
            double rise = AlphaUnitMax;
            if (ClearEnd > ClearStart)
            {
                if (d <= ClearStart) return AlphaUnitMin;
                if (d < ClearEnd) rise = SmoothStep((d - ClearStart) / (ClearEnd - ClearStart));
            }
            if (EdgeEnd <= EdgeStart || d <= EdgeStart) return rise;
            if (d >= EdgeEnd) return AlphaUnitMin;
            return rise * (AlphaUnitMax - SmoothStep((d - EdgeStart) / (EdgeEnd - EdgeStart)));
        }

        private static double SmoothStep(double t) => t * t * (3.0 - 2.0 * t);
    }

    // 안개 거리표 재사용 버퍼. Render 는 커서 헤일로 창의 UI 스레드에서만 호출되므로 단일 스레드 전제
    // (팝 애니메이션 매 프레임 재할당 회피).
    private static double[] s_fogLutAlpha = [];
    private static uint[] s_fogLutRgb = [];

    public static (int w, int h) Render(IntPtr ppvBits, CursorStyle style, CursorMetrics metrics) =>
        RenderCore(ppvBits, style, metrics, useFogLut: true);

    /// <summary>
    /// <paramref name="useFogLut"/>=false 는 안개를 샘플마다 직접 계산하는 정밀 경로 —
    /// 거리표 경로의 정확도 회귀 테스트 기준값 전용.
    /// </summary>
    internal static (int w, int h) RenderCore(
        IntPtr ppvBits, CursorStyle style, CursorMetrics metrics, bool useFogLut)
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
        double haloOpacity = Math.Clamp(style.HaloOpacity, AlphaUnitMin, AlphaUnitMax);

        double cx = w * 0.5;
        double cy = h * 0.5;

        double maxRing = style.CapsLockOn
            ? Math.Max(outerR, Math.Max(middleR, innerR))
            : Math.Max(middleR, innerR);

        double soft = Math.Clamp(style.MotionSoftness, AlphaUnitMin, AlphaUnitMax);
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

        // 커서 비움 — 안개 모드만(선명하게는 가는 고리라 안쪽이 이미 비어 있다). 팝 배율과 무관하게 고정 크기.
        double clearStart = 0.0, clearEnd = 0.0;
        if (soft > 0.0 && style.ClearRadiusLogicalPx > 0)
        {
            clearStart = DpiHelper.Scale(style.ClearRadiusLogicalPx, metrics.DpiScale);
            clearEnd = clearStart + DpiHelper.Scale(DefaultConfig.CursorClearFadeWidth, metrics.DpiScale);
        }

        // 그릴 범위가 DIB 내접원을 넘으면 경계 흐림 + 범위를 내접원으로 제한(모서리는 어차피 0).
        double inscribedR = Math.Min(w, h) * 0.5;
        double edgeStart = 0.0, edgeEnd = 0.0;
        if (maxOuterR > inscribedR)
        {
            double fadeWidth = Math.Max(fogSigma, EdgeFadeMinWidthPx);
            edgeStart = Math.Max(maxRing, inscribedR - fadeWidth);
            edgeEnd = inscribedR;
            maxOuterR = inscribedR;
        }
        RadialFade fade = new(clearStart, clearEnd, edgeStart, edgeEnd);
        double maxOuterRSq = maxOuterR * maxOuterR;

        bool fogLut = useFogLut && soft > 0.0 && fogSigma > 0.0;
        if (fogLut)
        {
            BuildFogLut(maxOuterR,
                outerR, middleR, innerR, baseCore, baseHalo, haloOpacity, soft, fogSigma, style.CapsLockOn,
                style.InnerColorArgb, style.MiddleColorArgb, style.OuterColorArgb,
                style.RingAlphaInner, style.RingAlphaMiddle, style.RingAlphaOuter, fade);
        }

        double minSigma = style.CapsLockOn ? CapsFogSigma(fogSigma) : fogSigma;
        if (fogLut && minSigma >= SingleSampleMinFogSigmaPx)
        {
            ShadeDibFogCenterSample(ppvBits, w, h, cx, cy, maxOuterRSq);
            return (w, h);
        }

        ShadeDib(ppvBits, w, h, cx, cy,
            outerR, middleR, innerR, baseCore, baseHalo, haloOpacity, soft, fogSigma, style.CapsLockOn,
            style.InnerColorArgb, style.MiddleColorArgb, style.OuterColorArgb,
            style.RingAlphaInner, style.RingAlphaMiddle, style.RingAlphaOuter,
            maxOuterRSq, fogLut, fade);

        return (w, h);
    }

    /// <summary>
    /// 넓은 안개 전용 — 픽셀 중심 1점만 거리표에서 조회 (<see cref="ShadeDib"/> 의 2×2 서브샘플 대신).
    /// 중심 <see cref="CenterSupersampleRadiusPx"/> 안쪽만 2×2. 4분면 대칭은 같다: 중심 오프셋 x+0.5-cx 와
    /// 거울 픽셀의 오프셋은 부호만 반대.
    /// </summary>
    private static unsafe void ShadeDibFogCenterSample(
        IntPtr ppvBits, int w, int h, double cx, double cy, double maxOuterRSq)
    {
        byte* bits = (byte*)ppvBits;
        int stride = w * DibSectionFactory.BytesPerPixel;
        int halfW = (w + 1) / 2;
        int halfH = (h + 1) / 2;
        const double centerRSq = CenterSupersampleRadiusPx * CenterSupersampleRadiusPx;

        for (int y = 0; y < halfH; y++)
        {
            double dy = y + HalfPixel - cy;
            double dy2 = dy * dy;
            if (dy2 > maxOuterRSq) continue;

            byte* rowTop = bits + (long)y * stride;
            byte* rowBot = bits + (long)(h - 1 - y) * stride;

            for (int x = 0; x < halfW; x++)
            {
                double dx = x + HalfPixel - cx;
                double distSq = dx * dx + dy2;
                if (distSq > maxOuterRSq) continue;

                double alpha;
                byte r, g, b;
                if (distSq < centerRSq)
                    LookupFogSupersampled(x, y, cx, cy, maxOuterRSq, out alpha, out r, out g, out b);
                else
                    LookupFog(Math.Sqrt(distSq), out alpha, out r, out g, out b);
                if (alpha < MinVisibleAlpha) continue;

                byte a = (byte)Math.Round(alpha * ByteChannelMax);
                int left = x * DibSectionFactory.BytesPerPixel;
                int right = (w - 1 - x) * DibSectionFactory.BytesPerPixel;
                WritePixel(rowTop + left, b, g, r, a);
                WritePixel(rowTop + right, b, g, r, a);
                WritePixel(rowBot + left, b, g, r, a);
                WritePixel(rowBot + right, b, g, r, a);
            }
        }
    }

    /// <summary>
    /// 픽셀 (x, y) 의 2×2 서브샘플 거리표 평균 — <see cref="ShadeDib"/> 와 같은 누적(알파 평균, 색은 알파 가중).
    /// </summary>
    private static void LookupFogSupersampled(
        int x, int y, double cx, double cy, double maxOuterRSq,
        out double alpha, out byte r, out byte g, out byte b)
    {
        double accumA = 0.0, accumR = 0.0, accumG = 0.0, accumB = 0.0;
        for (int sy = 0; sy < 2; sy++)
        {
            double dy = y + (sy == 0 ? SubSampleLow : SubSampleHigh) - cy;
            for (int sx = 0; sx < 2; sx++)
            {
                double dx = x + (sx == 0 ? SubSampleLow : SubSampleHigh) - cx;
                double distSq = dx * dx + dy * dy;
                if (distSq > maxOuterRSq) continue;
                LookupFog(Math.Sqrt(distSq), out double sa, out byte sr, out byte sg, out byte sb);
                if (sa <= 0.0) continue;
                accumA += sa;
                accumR += sa * sr;
                accumG += sa * sg;
                accumB += sa * sb;
            }
        }

        alpha = accumA * InvSubSampleCount;
        if (accumA <= 0.0)
        {
            r = 0; g = 0; b = 0;
            return;
        }
        r = (byte)Math.Round(accumR / accumA);
        g = (byte)Math.Round(accumG / accumA);
        b = (byte)Math.Round(accumB / accumA);
    }

    /// <summary>
    /// 안개 거리표 생성 — 거리 d = i / <see cref="FogLutStepsPerPx"/> 마다 <see cref="SampleRings"/> 결과(원들을 합친
    /// 알파·색 × 경계 흐림)를 저장. 픽셀 수(최대 수십만 × 4 서브샘플)만큼 하던 <c>Math.Exp</c> 를 반지름 길이만큼으로 줄인다.
    /// </summary>
    private static void BuildFogLut(
        double maxOuterR,
        double outerR, double middleR, double innerR,
        double coreHalf, double haloHalf, double haloOpacity,
        double soft, double fogSigma, bool capsOn,
        uint innerColorArgb, uint middleColorArgb, uint outerColorArgb,
        double ringAlphaInner, double ringAlphaMiddle, double ringAlphaOuter, RadialFade fade)
    {
        int count = (int)Math.Ceiling(maxOuterR * FogLutStepsPerPx) + FogLutTailEntries;
        if (s_fogLutAlpha.Length < count)
        {
            s_fogLutAlpha = new double[count];
            s_fogLutRgb = new uint[count];
        }

        for (int i = 0; i < count; i++)
        {
            double d = (double)i / FogLutStepsPerPx;
            SampleRings(d, outerR, middleR, innerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, capsOn,
                innerColorArgb, middleColorArgb, outerColorArgb,
                ringAlphaInner, ringAlphaMiddle, ringAlphaOuter, fade,
                out double a, out byte r, out byte g, out byte b);
            s_fogLutAlpha[i] = a;
            s_fogLutRgb[i] = ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    /// <summary>
    /// 거리표 조회 — 알파는 이웃 두 칸 선형 보간, 색은 가까운 칸 것. 가까운 칸 알파가 0(안개 끝)이면
    /// 그 칸의 검정 대신 이웃 칸 색을 쓴다. 호출자는 d ≤ maxOuterR 를 보장한다(표 길이 = maxOuterR × <see cref="FogLutStepsPerPx"/> 칸 + 여유).
    /// </summary>
    private static void LookupFog(double d, out double alpha, out byte r, out byte g, out byte b)
    {
        double pos = d * FogLutStepsPerPx;
        int i = (int)pos;
        double frac = pos - i;
        double a0 = s_fogLutAlpha[i];
        double a1 = s_fogLutAlpha[i + 1];
        alpha = a0 + (a1 - a0) * frac;
        bool nearFirst = frac < FogLutNearestSplit;
        if (nearFirst ? a0 <= 0.0 : a1 <= 0.0) nearFirst = !nearFirst;
        uint rgb = nearFirst ? s_fogLutRgb[i] : s_fogLutRgb[i + 1];
        r = (byte)(rgb >> 16);
        g = (byte)(rgb >> 8);
        b = (byte)rgb;
    }

    /// <summary>
    /// 거리 d 에서 원들을 합친 알파·색 × DIB 경계 흐림. Inner/Middle 은 더 진한 쪽, CAPS Outer 는 선명하게 모드면
    /// 더 진한 쪽 · 안개 모드면 over 로 쌓고 색은 알파⁴ 가중 섞기 (<see cref="SampleRingsUnfaded"/>).
    /// </summary>
    private static void SampleRings(
        double d,
        double outerR, double middleR, double innerR,
        double coreHalf, double haloHalf, double haloOpacity,
        double soft, double fogSigma, bool capsOn,
        uint innerColorArgb, uint middleColorArgb, uint outerColorArgb,
        double ringAlphaInner, double ringAlphaMiddle, double ringAlphaOuter, RadialFade fade,
        out double alpha, out byte r, out byte g, out byte b)
    {
        double fadeFactor = fade.Factor(d);
        alpha = 0.0;
        r = 0; g = 0; b = 0;
        if (fadeFactor <= AlphaUnitMin) return;
        SampleRingsUnfaded(d, outerR, middleR, innerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, capsOn,
            innerColorArgb, middleColorArgb, outerColorArgb,
            ringAlphaInner, ringAlphaMiddle, ringAlphaOuter,
            ref alpha, ref r, ref g, ref b);
        alpha *= fadeFactor;
    }

    private static void SampleRingsUnfaded(
        double d,
        double outerR, double middleR, double innerR,
        double coreHalf, double haloHalf, double haloOpacity,
        double soft, double fogSigma, bool capsOn,
        uint innerColorArgb, uint middleColorArgb, uint outerColorArgb,
        double ringAlphaInner, double ringAlphaMiddle, double ringAlphaOuter,
        ref double alpha, ref byte r, ref byte g, ref byte b)
    {
        EvaluateRing(d, innerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, innerColorArgb, ringAlphaInner,
            ref alpha, ref r, ref g, ref b);
        EvaluateRing(d, middleR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, middleColorArgb, ringAlphaMiddle,
            ref alpha, ref r, ref g, ref b);
        if (!capsOn) return;

        if (soft <= 0.0 || fogSigma <= 0.0)
        {
            // 선명하게 모드: 가는 고리끼리 떨어져 있으므로 가장 진한 원 하나만 (기존 동작).
            EvaluateRing(d, outerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, outerColorArgb, ringAlphaOuter,
                ref alpha, ref r, ref g, ref b);
            return;
        }

        // 안개 모드 CapsLock: 바깥 원(CapsLock 색)이 안쪽 원(현재 상태 색)과 겹친다. 「가장 진한 원 색」만
        // 쓰면 두 알파가 같아지는 지점에서 색이 뚝 바뀌어 원형 경계가 생기고(2026-11-04 사용자 보고), 알파 비율로
        // 섞으면 두 색이 탁하게 섞인다. → 진하기는 겹쳐 쌓고(over — 교차점의 알파 골 제거), 색은 알파의
        // 거듭제곱 가중으로 좁은 구간에서만 섞어 안쪽=현재 색, 바깥=CapsLock 색을 유지한다. 바깥 원은 σ 를 좁혀
        // (CapsFogSigma) 한/영 안개를 덮지 않고 그 바깥에 띠로 보이게 한다.
        double outerAlpha = 0.0;
        byte outerRed = 0, outerGreen = 0, outerBlue = 0;
        EvaluateRing(d, outerR, coreHalf, haloHalf, haloOpacity, soft, CapsFogSigma(fogSigma), outerColorArgb, ringAlphaOuter,
            ref outerAlpha, ref outerRed, ref outerGreen, ref outerBlue);
        if (outerAlpha <= 0.0) return;

        double wInner = Math.Pow(alpha, FogCapsColorSharpness);
        double wOuter = Math.Pow(outerAlpha, FogCapsColorSharpness);
        double wSum = wInner + wOuter;
        r = (byte)Math.Round((r * wInner + outerRed * wOuter) / wSum);
        g = (byte)Math.Round((g * wInner + outerGreen * wOuter) / wSum);
        b = (byte)Math.Round((b * wInner + outerBlue * wOuter) / wSum);
        alpha += outerAlpha * (AlphaUnitMax - alpha);
    }


    private static unsafe void ShadeDib(
        IntPtr ppvBits, int w, int h, double cx, double cy,
        double outerR, double middleR, double innerR,
        double coreHalf, double haloHalf, double haloOpacity,
        double soft, double fogSigma, bool capsOn,
        uint innerColorArgb, uint middleColorArgb, uint outerColorArgb,
        double ringAlphaInner, double ringAlphaMiddle, double ringAlphaOuter,
        double maxOuterRSq, bool fogLut, RadialFade fade)
    {
        byte* bits = (byte*)ppvBits;
        int stride = w * DibSectionFactory.BytesPerPixel;

        // 4분면 대칭: 중심 cx = w/2, cy = h/2 라 픽셀 x 와 거울 픽셀 w-1-x 의 서브샘플 dx 는 부호만 반대
        // ({x+0.25-cx, x+0.75-cx} ↔ 그 음수) — 거리 d 가 같아 결과도 같다. 왼쪽 위 4분면만 계산해 네 곳에 쓴다.
        // 홀수 변의 가운데 줄은 거울이 자기 자신이라 같은 값을 두 번 쓸 뿐이다.
        int halfW = (w + 1) / 2;
        int halfH = (h + 1) / 2;

        for (int y = 0; y < halfH; y++)
        {
            double dyTop = Math.Abs(y + SubSampleLow - cy);
            double dyBot = Math.Abs(y + SubSampleHigh - cy);
            double dyMin = Math.Min(dyTop, dyBot);
            if (dyMin * dyMin > maxOuterRSq)
                continue;

            byte* rowTop = bits + (long)y * stride;
            byte* rowBot = bits + (long)(h - 1 - y) * stride;

            for (int x = 0; x < halfW; x++)
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

                        double sampleAlpha;
                        byte sampleR, sampleG, sampleB;
                        if (fogLut)
                        {
                            LookupFog(d, out sampleAlpha, out sampleR, out sampleG, out sampleB);
                        }
                        else
                        {
                            SampleRings(d, outerR, middleR, innerR, coreHalf, haloHalf, haloOpacity, soft, fogSigma, capsOn,
                                innerColorArgb, middleColorArgb, outerColorArgb,
                                ringAlphaInner, ringAlphaMiddle, ringAlphaOuter, fade,
                                out sampleAlpha, out sampleR, out sampleG, out sampleB);
                        }

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
                    byte pb = (byte)Math.Round(accumB / accumA);
                    byte pg = (byte)Math.Round(accumG / accumA);
                    byte pr = (byte)Math.Round(accumR / accumA);
                    byte pa = (byte)Math.Round(avgAlpha * ByteChannelMax);
                    int left = x * DibSectionFactory.BytesPerPixel;
                    int right = (w - 1 - x) * DibSectionFactory.BytesPerPixel;
                    WritePixel(rowTop + left, pb, pg, pr, pa);
                    WritePixel(rowTop + right, pb, pg, pr, pa);
                    WritePixel(rowBot + left, pb, pg, pr, pa);
                    WritePixel(rowBot + right, pb, pg, pr, pa);
                }
            }
        }
    }

    private static unsafe void WritePixel(byte* p, byte b, byte g, byte r, byte a)
    {
        p[0] = b;
        p[1] = g;
        p[2] = r;
        p[3] = a;
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

    /// <summary>안개 CapsLock 바깥 원의 σ — 넓은 안개만 좁힌다(좁은 안개는 원래 한/영 안개를 덮지 않음).</summary>
    private static double CapsFogSigma(double fogSigma) =>
        fogSigma <= CapsFogMinSigmaPx ? fogSigma : Math.Max(fogSigma * CapsFogSigmaRatio, CapsFogMinSigmaPx);

    private static double Clamp01(double v)
    {
        if (v <= AlphaUnitMin) return AlphaUnitMin;
        if (v >= AlphaUnitMax) return AlphaUnitMax;
        return v;
    }
}
