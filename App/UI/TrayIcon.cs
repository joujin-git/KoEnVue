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
/// 헤일로 링 + 배지 디자인 — 텍스트 미표시, 배경색으로 IME 상태 구분.
/// </summary>
internal static class TrayIcon
{
    // 도형 = 커서 헤일로(바깥 링) + 플로팅 배지(안쪽 가로 사각형) — 제품의 두 표시 요소를 그대로
    // 은유한다. 좌클릭 순환 4단계는 **보이는 요소만 그리는 것**으로 표현한다:
    //   둘 다 보임 → 링 + 배지 / 배지만 → 배지 / 헤일로만 → 링 / 모두 숨김 → 배경색만
    // 취소선으로 덧그리지 않는 이유 — 도형이 넓어 같은 Fg 색 줄은 묻히고, 배경색으로 파내면
    // 도형이 조각나 읽기 어렵다. "있고 없음" 이 16px 에서 가장 빨리 읽힌다.
    // (P3: 매직 넘버 금지 — 모든 치수는 아이콘 크기 대비 비율 + 최소 픽셀)
    // 치수는 16px 실측으로 정했다 — 링을 아이콘 가장자리까지 키우면(inset 0) 사각형 모서리에서
    // 원이 평평하게 잘리고, 배지가 iconW*3/8 이면 링 안쪽에 닿아 답답해진다. 아래 비율이 링을
    // 온전히 유지하면서 배지 둘레에 여백이 남는 조합.
    private const int HaloEdgeInsetPx = 1;      // 링 바깥 반지름 = min(W,H)/2 - 1
    private const int HaloThicknessRatio = 8;   // 링 두께 = min(W,H) / 8
    private const int HaloThicknessMinPx = 2;
    private const int BadgeWidthRatio = 3;      // 배지 폭 = iconW / 3
    private const int BadgeHeightRatio = 5;     // 배지 높이 = iconH / 5
    private const int BadgeMinHeightPx = 3;

    // 배경은 둥근 모서리 정사각형 — 앱 아이콘(koenvue.ico)과 같은 인상. 모서리 바깥은 투명.
    private const double BackgroundCornerRatio = 0.22;  // 모서리 반지름 = min(W,H) * 0.22
    private const double BadgeCornerRatio = 0.25;       // 배지 모서리 반지름 = 배지 높이 * 0.25 (살짝만)

    // 링 바깥으로 번지는 후광 — "헤일로" 느낌을 내는 요소. 링에 붙은 쪽이 가장 진하고
    // 바깥으로 갈수록 빠르게 옅어진다(거듭제곱 감쇠).
    // **작은 아이콘에서는 생략한다** — 16px 에서 후광은 링 경계를 뿌옇게 흐려 오히려 선명도를
    // 떨어뜨린다(실측). 해상도별로 디테일을 달리하는 것은 아이콘 디자인의 통상 관행이며,
    // 앱 아이콘(koenvue.ico)도 같은 기준으로 32px 이상에만 후광을 넣는다.
    private const int GlowMinIconSize = 32;             // 이 크기 미만이면 후광 생략
    private const int GlowWidthRatio = 9;               // 후광 폭 = min(W,H) / 9
    private const double GlowMaxAlpha = 0.65;
    private const double GlowFalloff = 2.2;

    private const int AntiAliasSamples = 4;     // 경계 픽셀당 4x4 서브샘플

    /// <summary>
    /// ImeState별 배경색으로 헤일로 링 + 배지 아이콘을 생성한다.
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

        // 상태별 전경색 — 링·배지·후광이 공유. 테마 프리셋이 배경 대비 가독성을 보장하는
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
            //      없어 16px 원 둘레가 계단처럼 거칠어진다 — 링 경계 픽셀만 서브샘플링으로
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
    /// 커서 헤일로(바깥 링) + 플로팅 배지(안쪽 가로 사각형)를 32bpp DIB 픽셀에 직접 그린다 —
    /// 좌클릭 순환 단계에서 <b>보이는 요소만</b> 그리므로 네 단계가 도형 모양으로 구별된다.
    /// 모두 숨김 단계에서는 배경색만 남고, IME 상태는 그 배경색으로 계속 읽힌다.
    /// <para>
    /// GDI 의 <c>Ellipse</c> 를 쓰지 않는 이유 — GDI 는 안티앨리어싱을 하지 않아 16px 원 둘레가
    /// 계단처럼 거칠어진다. 링 경계에 걸친 픽셀만 <see cref="AntiAliasSamples"/>² 서브샘플로
    /// 커버리지를 재어 배경↔전경을 보간하면 같은 크기에서도 둘레가 매끄럽다. 배지는 정수 좌표
    /// 직사각형이라 경계가 픽셀에 딱 맞으므로 보간이 필요 없다.
    /// </para>
    /// </summary>
    /// <param name="bits">DIB 픽셀 버퍼 (32bpp BGRA, bottom-up).</param>
    private static unsafe void PaintIcon(IntPtr bits, int iconW, int iconH, uint fgColor, uint bgColor,
                                         IndicatorVisibility visibility)
    {
        (bool drawBadge, bool drawHalo) = Tray.GetShapes(visibility);

        // COLORREF(0x00BBGGRR) 채널 분해는 ColorHelper 단일 진실원 (P4)
        var (bgR, bgG, bgB) = ColorHelper.ColorRefToRgb(bgColor);
        var (fgR, fgG, fgB) = ColorHelper.ColorRefToRgb(fgColor);

        int side = Math.Min(iconW, iconH);
        double cx = iconW / 2.0, cy = iconH / 2.0;
        double rOuter = side / 2.0 - HaloEdgeInsetPx;
        double thick = Math.Max(side / (double)HaloThicknessRatio, HaloThicknessMinPx);
        double rInner = Math.Max(rOuter - thick, 1.0);
        double glowWidth = side / (double)GlowWidthRatio;
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

                double coverage = 0.0;
                if (drawHalo)
                {
                    coverage = RingCoverage(x, y, cx, cy, rInner, rOuter);
                    if (coverage < 1.0 && side >= GlowMinIconSize)
                    {
                        double glow = GlowIntensity(x, y, cx, cy, rOuter, glowWidth);
                        if (glow > coverage) coverage = glow;
                    }
                }
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

    /// <summary>
    /// 링 바깥으로 번지는 후광의 세기(0~1). 링에 접한 쪽이 가장 진하고 바깥으로 갈수록
    /// <see cref="GlowFalloff"/> 거듭제곱으로 옅어진다.
    /// </summary>
    private static double GlowIntensity(int px, int py, double cx, double cy, double rOuter, double width)
    {
        double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d <= rOuter || d >= rOuter + width) return 0.0;

        double t = (d - rOuter) / width;
        return GlowMaxAlpha * Math.Pow(1.0 - t, GlowFalloff);
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

    /// <summary>
    /// 픽셀 하나가 링([<paramref name="rInner"/>, <paramref name="rOuter"/>])에 덮인 비율(0~1).
    /// 경계에서 충분히 떨어진 픽셀은 서브샘플링 없이 0/1 로 즉시 판정한다.
    /// </summary>
    private static double RingCoverage(int px, int py, double cx, double cy, double rInner, double rOuter)
    {
        double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
        double d = Math.Sqrt(dx * dx + dy * dy);

        if (d > rOuter + 1.0 || d < rInner - 1.0) return 0.0;
        if (d > rInner + 1.0 && d < rOuter - 1.0) return 1.0;

        int hit = 0;
        for (int sy = 0; sy < AntiAliasSamples; sy++)
        {
            for (int sx = 0; sx < AntiAliasSamples; sx++)
            {
                double fx = px + (sx + 0.5) / AntiAliasSamples - cx;
                double fy = py + (sy + 0.5) / AntiAliasSamples - cy;
                double sd = Math.Sqrt(fx * fx + fy * fy);
                if (sd >= rInner && sd <= rOuter) hit++;
            }
        }
        return hit / (double)(AntiAliasSamples * AntiAliasSamples);
    }

    private static byte Blend(byte from, byte to, double t) => (byte)(from + (to - from) * t + 0.5);
}
