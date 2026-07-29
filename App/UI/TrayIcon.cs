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

            // 6. 헤일로 링 + 배지 도형 — 좌클릭 순환 단계에서 **보이는 요소만** 그린다.
            //    단계 판독은 Tray.GetVisibility, 요소 선택은 Tray.GetShapes 단일 진실원.
            DrawBadgeHalo(memDC, iconW, iconH, fgColor, bgColor, Tray.GetVisibility(config));

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
    /// 커서 헤일로(바깥 링) + 플로팅 배지(안쪽 가로 사각형)를 그린다 — 좌클릭 순환 단계에서
    /// <b>보이는 요소만</b> 그리므로 네 단계가 도형 모양으로 구별된다.
    /// 링은 Fg 원을 채운 뒤 안쪽을 <paramref name="bgColor"/> 원으로 파내 만든다.
    /// 모두 숨김 단계에서는 아무 도형도 그리지 않아 배경색만 남는다 — IME 상태는 그 배경색으로
    /// 계속 읽히므로 "앱은 살아 있고 표시만 전부 껐다" 가 드러난다.
    /// </summary>
    private static void DrawBadgeHalo(IntPtr hdc, int iconW, int iconH, uint fgColor, uint bgColor,
                                      IndicatorVisibility visibility)
    {
        (bool drawBadge, bool drawHalo) = Tray.GetShapes(visibility);

        if (drawHalo)
        {
            int side = Math.Min(iconW, iconH);
            int cx = iconW / 2, cy = iconH / 2;
            int rOuter = side / 2 - HaloEdgeInsetPx;
            int thick = Math.Max(side / HaloThicknessRatio, HaloThicknessMinPx);
            int rInner = Math.Max(rOuter - thick, 1);

            using (var outer = new SolidFillScope(hdc, fgColor))
                Gdi32.Ellipse(hdc, cx - rOuter, cy - rOuter, cx + rOuter, cy + rOuter);
            using (var inner = new SolidFillScope(hdc, bgColor))
                Gdi32.Ellipse(hdc, cx - rInner, cy - rInner, cx + rInner, cy + rInner);
        }

        if (drawBadge)
        {
            int w = iconW / BadgeWidthRatio;
            int h = Math.Max(iconH / BadgeHeightRatio, BadgeMinHeightPx);
            int x = (iconW - w) / 2;
            int y = (iconH - h) / 2;

            using var _ = new SolidFillScope(hdc, fgColor);
            Gdi32.Rectangle(hdc, x, y, x + w, y + h);
        }
    }
}
