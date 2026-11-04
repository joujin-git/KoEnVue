using System.Runtime.InteropServices;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI;

/// <summary>
/// GDI 기반 트레이 아이콘 동적 생성.
/// 헤일로 안개 고리 + 배지 디자인 — 텍스트 미표시, 배경색으로 IME 상태 구분.
/// </summary>
internal static class TrayIcon
{
    // 도형 = 커서 헤일로(안개 고리) + 한/영 배지(안쪽 가로 사각형) — 제품의 두 표시 요소를 그대로
    // 은유한다. 좌클릭 순환 4단계는 **보이는 요소만 그리는 것**으로 표현한다:
    //   둘 다 보임 → 고리 + 배지 / 배지만 → 배지 / 헤일로만 → 고리 / 모두 숨김 → 배경색만
    // 취소선으로 덧그리지 않는 이유 — 도형이 넓어 같은 Fg 색 줄은 묻히고, 배경색으로 파내면
    // 도형이 조각나 읽기 어렵다. "있고 없음" 이 16px 에서 가장 빨리 읽힌다.
    // (P3: 매직 넘버 금지 — 모든 치수는 아이콘 크기 대비 비율 + 최소 픽셀)
    private const int BadgeWidthRatio = 3;      // 배지 폭 = iconW / 3
    private const int BadgeHeightRatio = 5;     // 배지 높이 = iconH / 5
    private const int BadgeMinHeightPx = 3;

    // 배경은 둥근 모서리 정사각형 — 앱 아이콘(koenvue.ico)과 같은 인상. 모서리 바깥은 투명.
    private const double BackgroundCornerRatio = 0.22;  // 모서리 반지름 = min(W,H) * 0.22
    private const double BadgeCornerRatio = 0.25;       // 배지 모서리 반지름 = 배지 높이 * 0.25 (살짝만)

    // 헤일로 = 가우시안 안개 고리 (2026-11-04) — 기본 커서 헤일로가 넓은 안개(「흐릿하게」)로 바뀐 것에 맞춰
    // 선명한 링+후광을 대체. 앱 아이콘(koenvue.ico)과 같은 비율: 고리 중심 반지름 0.34·σ 0.085·최대 진하기 0.95
    // (모두 min(W,H) 대비). 고리 안쪽이 비어 있어 「커서 주변 비움」과도 모양이 맞는다.
    private const double FogRingRadiusRatio = 0.34;
    private const double FogSigmaRatio = 0.085;
    private const double FogMaxAlpha = 0.95;
    private const double FogSigmaCutoff = 3.5;          // σ 의 이 배수 밖은 0
    // 작은 아이콘(이 크기 미만 — 100~150% DPI 트레이 16~24px)은 안개가 뭉개지므로 σ 를 좁히고
    // 진하기를 1 로, 고리 중심에 가는 선을 겹쳐 윤곽을 살린다 (koenvue.ico 16~24px 와 동일).
    private const int SmallIconMaxExclusive = 32;
    private const double SmallFogSigmaRatio = 0.06;
    private const double SmallFogMaxAlpha = 1.0;
    private const double SmallCoreLineWidthPx = 1.6;

    private const int AntiAliasSamples = 4;     // 픽셀당 4x4 서브샘플

    /// <summary>
    /// ImeState별 배경색으로 헤일로 안개 고리 + 배지 아이콘을 생성한다.
    /// 호출자가 반환된 SafeIconHandle의 수명을 관리한다.
    /// </summary>
    internal static unsafe SafeIconHandle CreateIcon(ImeState state, AppConfig config)
    {
        // 1. 시스템이 요구하는 소형 아이콘 크기 조회 (하드코딩 금지, P3)
        int iconW = User32.GetSystemMetrics(Win32Constants.SM_CXSMICON);
        int iconH = User32.GetSystemMetrics(Win32Constants.SM_CYSMICON);

        // 상태별 배경색 (P4: ColorHelper 사용 강제)
        string bgHex = state switch
        {
            ImeState.Hangul => config.HangulBg,
            ImeState.English => config.EnglishBg,
            ImeState.NonKorean => config.NonKoreanBg,
            _ => config.EnglishBg,
        };
        uint bgColor = ColorHelper.HexToColorRef(bgHex);

        // 상태별 전경색 — 안개 고리·배지가 공유. 테마 프리셋이 배경 대비 가독성을 보장하는
        // Fg 쌍을 세팅하므로 아이콘 내부 도형 색을 여기에 위임 (pastel 테마의 저대비 방지).
        string fgHex = state switch
        {
            ImeState.Hangul => config.HangulFg,
            ImeState.English => config.EnglishFg,
            ImeState.NonKorean => config.NonKoreanFg,
            _ => config.EnglishFg,
        };
        uint fgColor = ColorHelper.HexToColorRef(fgHex);

        // GDI 중간 객체 — try/finally로 누수 방지
        IntPtr memDC = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hMask = IntPtr.Zero;
        IntPtr hOldBitmap = IntPtr.Zero;

        try
        {
            // 2. 메모리 DC 생성
            memDC = Gdi32.CreateCompatibleDC(IntPtr.Zero);

            // 3. 32bpp DIB 섹션 생성 (color bitmap)
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = iconW,
                biHeight = iconH, // bottom-up
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32Constants.BI_RGB,
            };
            hBitmap = Gdi32.CreateDIBSection(memDC, ref bmi, Win32Constants.DIB_RGB_COLORS,
                out IntPtr ppvBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || ppvBits == IntPtr.Zero)
            {
                Logger.Warning("Failed to create tray icon DIB section");
                return new SafeIconHandle(IntPtr.Zero, ownsHandle: false);
            }

            // 4. DIB를 DC에 선택
            hOldBitmap = Gdi32.SelectObject(memDC, hBitmap);

            // 5~6. 배경 + 도형을 DIB 픽셀에 **직접** 쓴다. GDI 의 Ellipse 는 안티앨리어싱이
            //      없어 16px 원 둘레가 계단처럼 거칠어진다 — 안개 고리·둥근 모서리를 서브샘플링으로
            //      커버리지를 구해 배경↔전경을 보간한다.
            //      단계 판독은 Tray.GetVisibility, 요소 선택은 Tray.GetShapes 단일 진실원.
            PaintIcon(ppvBits, iconW, iconH, fgColor, bgColor, Tray.GetVisibility(config));

            // 이전 비트맵 복원 (SelectObject 전 필수)
            Gdi32.SelectObject(memDC, hOldBitmap);
            hOldBitmap = IntPtr.Zero;

            // 7. 마스크 비트맵 생성 (monochrome, 모두 0 = 불투명)
            hMask = Gdi32.CreateCompatibleBitmap(memDC, iconW, iconH);

            // 8. ICONINFO → CreateIconIndirect → HICON
            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = hBitmap,
                hbmMask = hMask,
            };
            IntPtr hIcon = User32.CreateIconIndirect(ref iconInfo);

            if (hIcon == IntPtr.Zero)
            {
                Logger.Warning("Failed to create tray icon");
                return new SafeIconHandle(IntPtr.Zero, ownsHandle: false);
            }

            // 10. SafeIconHandle로 래핑
            return new SafeIconHandle(hIcon, ownsHandle: true);
        }
        finally
        {
            // 9. 임시 GDI 리소스 정리
            if (hOldBitmap != IntPtr.Zero)
                Gdi32.SelectObject(memDC, hOldBitmap);
            if (hMask != IntPtr.Zero)
                Gdi32.DeleteObject(hMask);
            if (hBitmap != IntPtr.Zero)
                Gdi32.DeleteObject(hBitmap);
            if (memDC != IntPtr.Zero)
                Gdi32.DeleteDC(memDC);
        }
    }

    /// <summary>
    /// 커서 헤일로(안개 고리) + 한/영 배지(안쪽 가로 사각형)를 32bpp DIB 픽셀에 직접 그린다 —
    /// 좌클릭 순환 단계에서 <b>보이는 요소만</b> 그리므로 네 단계가 도형 모양으로 구별된다.
    /// 모두 숨김 단계에서는 배경색만 남고, IME 상태는 그 배경색으로 계속 읽힌다.
    /// <para>
    /// GDI 도형 API 를 쓰지 않는 이유 — GDI 는 안티앨리어싱을 하지 않아 16px 원 둘레가 계단처럼
    /// 거칠어진다. 고리는 <see cref="AntiAliasSamples"/>² 서브샘플 평균, 둥근 모서리는 경계 픽셀만
    /// 서브샘플로 커버리지를 재어 배경↔전경을 보간한다.
    /// </para>
    /// </summary>
    /// <param name="bits">DIB 픽셀 버퍼 (32bpp BGRA, bottom-up).</param>
    internal static unsafe void PaintIcon(IntPtr bits, int iconW, int iconH, uint fgColor, uint bgColor,
                                         IndicatorVisibility visibility)
    {
        (bool drawBadge, bool drawHalo) = Tray.GetShapes(visibility);

        // COLORREF(0x00BBGGRR) 채널 분해는 ColorHelper 단일 진실원 (P4)
        var (bgR, bgG, bgB) = ColorHelper.ColorRefToRgb(bgColor);
        var (fgR, fgG, fgB) = ColorHelper.ColorRefToRgb(fgColor);

        int side = Math.Min(iconW, iconH);
        double cx = iconW / 2.0, cy = iconH / 2.0;
        bool small = side < SmallIconMaxExclusive;
        var fog = new FogRing(
            Radius: side * FogRingRadiusRatio,
            Sigma: side * (small ? SmallFogSigmaRatio : FogSigmaRatio),
            MaxAlpha: small ? SmallFogMaxAlpha : FogMaxAlpha,
            CoreHalfWidth: small ? SmallCoreLineWidthPx * 0.5 : 0.0);
        double corner = side * BackgroundCornerRatio;

        double badgeW = iconW / (double)BadgeWidthRatio;
        double badgeH = Math.Max(iconH / (double)BadgeHeightRatio, BadgeMinHeightPx);
        double badgeX = (iconW - badgeW) / 2.0, badgeY = (iconH - badgeH) / 2.0;
        double badgeRadius = badgeH * BadgeCornerRatio;

        byte* buf = (byte*)bits;
        int stride = iconW * DibSectionFactory.BytesPerPixel;

        for (int y = 0; y < iconH; y++)
        {
            byte* row = buf + (iconH - 1 - y) * stride;  // bottom-up DIB
            for (int x = 0; x < iconW; x++)
            {
                byte* px = row + x * DibSectionFactory.BytesPerPixel;

                // 둥근 모서리 배경 — 바깥은 완전 투명
                double bgCoverage = RoundedRectCoverage(x, y, 0, 0, iconW, iconH, corner);
                if (bgCoverage <= 0.0)
                {
                    px[0] = px[1] = px[2] = px[3] = 0;
                    continue;
                }

                double coverage = drawHalo ? FogRingCoverage(x, y, cx, cy, fog) : 0.0;
                if (drawBadge)
                {
                    double badge = RoundedRectCoverage(x, y, badgeX, badgeY,
                                                       badgeX + badgeW, badgeY + badgeH, badgeRadius);
                    if (badge > coverage) coverage = badge;
                }

                px[0] = Blend(bgB, fgB, coverage);
                px[1] = Blend(bgG, fgG, coverage);
                px[2] = Blend(bgR, fgR, coverage);
                px[3] = (byte)(byte.MaxValue * bgCoverage + 0.5);
            }
        }
    }

    /// <summary>안개 고리 치수 (px) — 중심 반지름, σ, 최대 진하기, 겹치는 선의 반폭(0 = 선 없음).</summary>
    private readonly record struct FogRing(double Radius, double Sigma, double MaxAlpha, double CoreHalfWidth)
    {
        /// <summary>중심 거리 d 에서의 진하기(0~1) — 가우시안 안개와 가는 선 중 큰 쪽.</summary>
        public double Intensity(double d)
        {
            double off = Math.Abs(d - Radius);
            if (off < CoreHalfWidth) return 1.0;
            double t = off / Sigma;
            return t >= FogSigmaCutoff ? 0.0 : MaxAlpha * Math.Exp(-0.5 * t * t);
        }
    }

    /// <summary>
    /// 픽셀 하나의 안개 고리 진하기(0~1) — <see cref="AntiAliasSamples"/>² 서브샘플 평균.
    /// 아이콘이 작아(수십 px) 모든 픽셀을 서브샘플해도 비용이 무시할 만하다.
    /// </summary>
    private static double FogRingCoverage(int px, int py, double cx, double cy, FogRing fog)
    {
        double sum = 0.0;
        for (int sy = 0; sy < AntiAliasSamples; sy++)
        {
            for (int sx = 0; sx < AntiAliasSamples; sx++)
            {
                double fx = px + (sx + 0.5) / AntiAliasSamples - cx;
                double fy = py + (sy + 0.5) / AntiAliasSamples - cy;
                sum += fog.Intensity(Math.Sqrt(fx * fx + fy * fy));
            }
        }
        return sum / (AntiAliasSamples * AntiAliasSamples);
    }

    /// <summary>
    /// 픽셀 하나가 둥근 모서리 사각형에 덮인 비율(0~1). 네 꼭지점이 모두 안/밖이면 서브샘플링
    /// 없이 즉시 판정하고, 경계에 걸친 픽셀만 <see cref="AntiAliasSamples"/>² 로 재어 보간한다.
    /// </summary>
    private static double RoundedRectCoverage(int px, int py,
                                              double x0, double y0, double x1, double y1, double radius)
    {
        bool c00 = InRoundedRect(px, py, x0, y0, x1, y1, radius);
        bool c10 = InRoundedRect(px + 1, py, x0, y0, x1, y1, radius);
        bool c01 = InRoundedRect(px, py + 1, x0, y0, x1, y1, radius);
        bool c11 = InRoundedRect(px + 1, py + 1, x0, y0, x1, y1, radius);
        if (c00 && c10 && c01 && c11) return 1.0;
        if (!c00 && !c10 && !c01 && !c11)
        {
            // 네 꼭지점이 모두 밖이어도 픽셀 중심이 안이면 경계에 걸친 것 — 서브샘플로 넘어간다
            if (!InRoundedRect(px + 0.5, py + 0.5, x0, y0, x1, y1, radius)) return 0.0;
        }

        int hit = 0;
        for (int sy = 0; sy < AntiAliasSamples; sy++)
        {
            for (int sx = 0; sx < AntiAliasSamples; sx++)
            {
                double fx = px + (sx + 0.5) / AntiAliasSamples;
                double fy = py + (sy + 0.5) / AntiAliasSamples;
                if (InRoundedRect(fx, fy, x0, y0, x1, y1, radius)) hit++;
            }
        }
        return hit / (double)(AntiAliasSamples * AntiAliasSamples);
    }

    private static bool InRoundedRect(double x, double y,
                                      double x0, double y0, double x1, double y1, double radius)
    {
        if (x < x0 || x > x1 || y < y0 || y > y1) return false;
        if (radius <= 0.0) return true;

        double cxL = x0 + radius, cxR = x1 - radius;
        double cyT = y0 + radius, cyB = y1 - radius;
        double dx = x < cxL ? cxL - x : (x > cxR ? x - cxR : 0.0);
        double dy = y < cyT ? cyT - y : (y > cyB ? y - cyB : 0.0);
        return dx * dx + dy * dy <= radius * radius;
    }

    private static byte Blend(byte from, byte to, double t) => (byte)(from + (to - from) * t + 0.5);
}
