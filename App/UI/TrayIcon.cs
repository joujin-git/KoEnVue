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
/// 캐럿+점(caret_dot) 디자인 — 텍스트 미표시, 배경색으로 IME 상태 구분.
/// </summary>
internal static class TrayIcon
{
    // 캐럿+점 도형 비율/최소크기 (P3: 매직 넘버 금지)
    private const int CaretWidthRatio = 8;     // 캐럿 너비 = iconW / 8
    private const int CaretMinWidth = 2;       // 캐럿 최소 너비 (px)
    private const int CaretHeightNum = 5;      // 캐럿 높이 = iconH * 5/8
    private const int CaretHeightDen = 8;
    private const int CaretOffsetRatio = 8;    // 캐럿 X 오프셋 = iconW / 8
    private const int DotSizeRatio = 4;        // 점 크기 = iconW / 4
    private const int DotMinSize = 3;          // 점 최소 크기 (px)
    private const int DotGapMinPx = 1;         // 점-캐럿 최소 간격 (px)
    private const int CaretYOffsetPx = 1;      // 시각 보정: 캐럿+점이 위로 떠 보이는 현상을 1px 아래로 보정

    // 단일 취소선 (UserHidden=true 시 캐럿+점 위에 중첩). 아이콘 세로 중앙 수평 1줄, 굵게.
    // 16px 에서 4px, 20px 에서 5px — 캐럿+점 도형을 가로지르면서도 형체는 읽히는 두께.
    private const int StrikeThicknessRatio = 4;  // 두께 = iconH / 4
    private const int StrikeThicknessMinPx = 3;
    private const int StrikeEdgeInsetPx = 1;     // 좌우 엣지 1px 여백

    /// <summary>
    /// ImeState별 배경색으로 캐럿+점 아이콘을 생성한다.
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

        // 상태별 전경색 — 캐럿+점과 취소선이 공유. 테마 프리셋이 배경 대비 가독성을 보장하는
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
        IntPtr hBrush = IntPtr.Zero;

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
                out _, IntPtr.Zero, 0);

            // 4. DIB를 DC에 선택
            hOldBitmap = Gdi32.SelectObject(memDC, hBitmap);

            // 5. 배경색으로 전체 영역 채움
            hBrush = Gdi32.CreateSolidBrush(bgColor);
            var rect = new RECT { Left = 0, Top = 0, Right = iconW, Bottom = iconH };
            User32.FillRect(memDC, ref rect, hBrush);

            // 6. 캐럿+점 도형 (Fg 색상)
            DrawCaretDot(memDC, iconW, iconH, fgColor);

            // 6a. 사용자 숨김 상태면 단일 취소선 중첩 (Fg 색상, 볼드)
            if (config.UserHidden)
                DrawStrikeThrough(memDC, iconW, iconH, fgColor);

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
            if (hBrush != IntPtr.Zero)
                Gdi32.DeleteObject(hBrush);
            if (hMask != IntPtr.Zero)
                Gdi32.DeleteObject(hMask);
            if (hBitmap != IntPtr.Zero)
                Gdi32.DeleteObject(hBitmap);
            if (memDC != IntPtr.Zero)
                Gdi32.DeleteDC(memDC);
        }
    }

    /// <summary>
    /// 캐럿(세로바) + 점 도형을 Fg 색으로 그린다.
    /// 아이콘 중앙 부근에 배치.
    /// </summary>
    private static void DrawCaretDot(IntPtr hdc, int iconW, int iconH, uint fgColor)
    {
        IntPtr hFgBrush = Gdi32.CreateSolidBrush(fgColor);
        IntPtr hNullPen = Gdi32.GetStockObject(Win32Constants.NULL_PEN);
        IntPtr hOldBrush = Gdi32.SelectObject(hdc, hFgBrush);
        IntPtr hOldPen = Gdi32.SelectObject(hdc, hNullPen);

        try
        {
            // 캐럿 (세로바): 아이콘 중앙 왼쪽에 배치
            int caretW = Math.Max(iconW / CaretWidthRatio, CaretMinWidth);
            int caretH = iconH * CaretHeightNum / CaretHeightDen;
            int caretX = (iconW - caretW) / 2 - iconW / CaretOffsetRatio;
            int caretY = (iconH - caretH + 1) / 2 + CaretYOffsetPx;
            Gdi32.Rectangle(hdc, caretX, caretY, caretX + caretW, caretY + caretH);

            // 점 (dot): 캐럿 오른쪽 하단에 작은 원
            int dotSize = Math.Max(iconW / DotSizeRatio, DotMinSize);
            int dotX = caretX + caretW + Math.Max(iconW / CaretOffsetRatio, DotGapMinPx);
            int dotY = caretY + caretH - dotSize;
            Gdi32.Ellipse(hdc, dotX, dotY, dotX + dotSize, dotY + dotSize);
        }
        finally
        {
            Gdi32.SelectObject(hdc, hOldPen);
            Gdi32.SelectObject(hdc, hOldBrush);
            Gdi32.DeleteObject(hFgBrush);
        }
    }

    /// <summary>
    /// UserHidden=true 시 캐럿+점 위에 Fg 색 단일 수평 취소선을 중첩한다.
    /// 아이콘 세로 중앙에 두껍게 1줄 — "숨김" 상태 시인성 확보.
    /// </summary>
    private static void DrawStrikeThrough(IntPtr hdc, int iconW, int iconH, uint fgColor)
    {
        IntPtr hFgBrush = Gdi32.CreateSolidBrush(fgColor);
        IntPtr hNullPen = Gdi32.GetStockObject(Win32Constants.NULL_PEN);
        IntPtr hOldBrush = Gdi32.SelectObject(hdc, hFgBrush);
        IntPtr hOldPen = Gdi32.SelectObject(hdc, hNullPen);

        try
        {
            int thick = Math.Max(iconH / StrikeThicknessRatio, StrikeThicknessMinPx);
            int left = StrikeEdgeInsetPx;
            int right = iconW - StrikeEdgeInsetPx;

            // 단일선 — Y 중심 = iconH / 2
            int centerY = iconH / 2;
            int y = centerY - thick / 2;
            Gdi32.Rectangle(hdc, left, y, right, y + thick);
        }
        finally
        {
            Gdi32.SelectObject(hdc, hOldPen);
            Gdi32.SelectObject(hdc, hOldBrush);
            Gdi32.DeleteObject(hFgBrush);
        }
    }
}
