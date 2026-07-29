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

    // 취소선 — 숨김 "범위" 를 선 개수로 표현한다 (캐럿+점 위에 Fg 색으로 중첩).
    //   단일선 = 플로팅 배지와 커서 헤일로 중 **하나만** 숨김
    //   이중선 = **둘 다** 숨김 (트레이 좌클릭 일괄 숨김의 결과)
    // 16px 에서 길이(짧은 선/긴 선)로 구분하면 판별이 어려워 개수 축을 택했다.
    // 단일선: 16px 에서 4px, 20px 에서 5px — 도형을 가로지르면서도 형체는 읽히는 두께.
    private const int StrikeThicknessRatio = 4;  // 두께 = iconH / 4
    private const int StrikeThicknessMinPx = 3;
    private const int StrikeEdgeInsetPx = 1;     // 좌우 엣지 1px 여백

    // 이중선: 두 선 + 간격이 아이콘 높이에 들어가야 하므로 단일선보다 얇게 잡는다.
    // 16px → 3px·간격 2px·총 8px, 20px → 3px·간격 2px·총 8px (상하 여백 확보).
    private const int DoubleStrikeThicknessRatio = 6;  // 각 선 두께 = iconH / 6
    private const int DoubleStrikeThicknessMinPx = 2;
    private const int DoubleStrikeGapRatio = 8;        // 두 선 사이 간격 = iconH / 8
    private const int DoubleStrikeGapMinPx = 2;

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

            // 6a. 숨김 상태면 취소선 중첩 (Fg 색상, 볼드) — 선 개수로 숨김 범위를 표현.
            //     하나만 숨김 = 단일선, 배지·헤일로 둘 다 숨김 = 이중선.
            bool badgeHidden = config.UserHidden;
            bool cursorHidden = !config.CursorIndicatorEnabled;
            if (badgeHidden || cursorHidden)
                DrawStrikeThrough(memDC, iconW, iconH, fgColor, doubleLine: badgeHidden && cursorHidden);

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
    /// 단색 채우기 GDI 컨텍스트 — solid brush + NULL_PEN 을 선택하고, using 종료 시 원래
    /// brush/pen 을 복원하고 brush 핸들을 해제한다. DrawCaretDot/DrawStrikeThrough 가 동일한
    /// prologue/finally 보일러를 공유한다 (AUDIT DUP-12). NULL_PEN 은 stock object 라 복원만 하고
    /// DeleteObject 하지 않으며, 정리 대상은 CreateSolidBrush 핸들뿐이다.
    /// readonly ref struct — 스택 전용, 힙 할당 0 (NativeAOT 린 앱 부합 · 람다 클로저 회피).
    /// </summary>
    private readonly ref struct SolidFillScope
    {
        private readonly IntPtr _hdc;
        private readonly IntPtr _hBrush;
        private readonly IntPtr _hOldBrush;
        private readonly IntPtr _hOldPen;

        public SolidFillScope(IntPtr hdc, uint fgColor)
        {
            _hdc = hdc;
            _hBrush = Gdi32.CreateSolidBrush(fgColor);
            IntPtr hNullPen = Gdi32.GetStockObject(Win32Constants.NULL_PEN);
            _hOldBrush = Gdi32.SelectObject(hdc, _hBrush);
            _hOldPen = Gdi32.SelectObject(hdc, hNullPen);
        }

        public void Dispose()
        {
            Gdi32.SelectObject(_hdc, _hOldPen);
            Gdi32.SelectObject(_hdc, _hOldBrush);
            Gdi32.DeleteObject(_hBrush);
        }
    }

    /// <summary>
    /// 캐럿(세로바) + 점 도형을 Fg 색으로 그린다.
    /// 아이콘 중앙 부근에 배치.
    /// </summary>
    private static void DrawCaretDot(IntPtr hdc, int iconW, int iconH, uint fgColor)
    {
        using var _ = new SolidFillScope(hdc, fgColor);

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

    /// <summary>
    /// 숨김 상태일 때 캐럿+점 위에 Fg 색 수평 취소선을 중첩한다 — 아이콘 세로 중앙 정렬.
    /// <paramref name="doubleLine"/> 이면 두 줄(배지·헤일로 **둘 다** 숨김), 아니면 한 줄
    /// (**하나만** 숨김). 선 개수가 곧 숨김 범위다.
    /// </summary>
    private static void DrawStrikeThrough(IntPtr hdc, int iconW, int iconH, uint fgColor, bool doubleLine)
    {
        using var _ = new SolidFillScope(hdc, fgColor);

        int left = StrikeEdgeInsetPx;
        int right = iconW - StrikeEdgeInsetPx;
        int centerY = iconH / 2;

        if (!doubleLine)
        {
            int thick = Math.Max(iconH / StrikeThicknessRatio, StrikeThicknessMinPx);
            int y = centerY - thick / 2;
            Gdi32.Rectangle(hdc, left, y, right, y + thick);
            return;
        }

        // 이중선 — 두 줄 + 간격을 하나의 블록으로 보고 그 블록을 세로 중앙에 맞춘다.
        int lineThick = Math.Max(iconH / DoubleStrikeThicknessRatio, DoubleStrikeThicknessMinPx);
        int gap = Math.Max(iconH / DoubleStrikeGapRatio, DoubleStrikeGapMinPx);
        int blockH = lineThick * 2 + gap;
        int top = centerY - blockH / 2;

        Gdi32.Rectangle(hdc, left, top, right, top + lineThick);
        Gdi32.Rectangle(hdc, left, top + lineThick + gap, right, top + blockH);
    }
}
