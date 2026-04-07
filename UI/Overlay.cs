using System.Runtime.InteropServices;
using KoEnVue.Config;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.UI;

/// <summary>
/// GDI 기반 오버레이 렌더링 + 위치 계산.
/// 모든 호출은 메인 스레드에서만 수행.
/// </summary>
internal static class Overlay
{
    // ================================================================
    // GDI 리소스 상태
    // ================================================================

    private static IntPtr _hwndOverlay;
    private static IntPtr _memDC;
    private static SafeBitmapHandle? _currentBitmap;
    private static SafeFontHandle? _currentFont;
    private static IntPtr _ppvBits;
    private static IntPtr _nullPen;

    // DPI 캐시
    private static double _currentDpiScale = 1.0;
    private static uint _currentDpiY = DpiHelper.BASE_DPI;

    // DIB 크기 캐시
    private static int _currentWidth;
    private static int _currentHeight;

    // 기본 크기 (DPI 미적용)
    private static int _baseWidth;
    private static int _baseHeight;

    // F-S01: 라벨 고정 너비
    private static int _fixedLabelWidth;

    // 폰트 캐시 키
    private static string _cachedFontFamily = "";
    private static int _cachedFontSize;
    private static FontWeight _cachedFontWeight;
    private static double _cachedFontDpiScale;

    // 위치/상태 추적
    private static readonly OverlayState _overlayState = new();
    private static ImeState? _lastRenderedState;
    private static int _lastX;
    private static int _lastY;
    private static byte _lastAlpha;

    // 디버그 오버레이
    private static volatile DebugInfo? _debugInfo;

    // 디버그 오버레이 상수
    private const int DebugOverlayLineCount = 5;
    private const int DebugOverlayLineHeight = 12;
    private const int DebugOverlayWidth = 140;
    private const int DebugOverlayFontSize = 8;

    // ================================================================
    // 초기화 / 해제
    // ================================================================

    public static void Initialize(IntPtr hwndOverlay, AppConfig config)
    {
        _hwndOverlay = hwndOverlay;
        _memDC = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        _nullPen = Gdi32.GetStockObject(Win32Constants.NULL_PEN);
        EnsureResources(config);
    }

    public static void Dispose()
    {
        _currentBitmap?.Dispose();
        _currentBitmap = null;
        _currentFont?.Dispose();
        _currentFont = null;
        if (_memDC != IntPtr.Zero)
        {
            Gdi32.DeleteDC(_memDC);
            _memDC = IntPtr.Zero;
        }
    }

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 위치 계산 + 렌더 + UpdateLayeredWindow. ShowWindow는 호출하지 않음 (Animation이 관리).
    /// </summary>
    public static void Show(int caretX, int caretY, int caretH, ImeState state, AppConfig config)
    {
        var pos = CalculateIndicatorPosition(caretX, caretY, caretH, config);
        EnsureResources(config);
        RenderIndicator(state, config);

        // 디버그 오버레이 렌더링 (인디케이터 렌더 후, UpdateLayeredWindow 전)
        if (config.Advanced.DebugOverlay && _debugInfo is { } dbg)
            RenderDebugOverlay(dbg);

        UpdateOverlay(pos.x, pos.y, _currentWidth, _currentHeight, _lastAlpha);
        _lastX = pos.x;
        _lastY = pos.y;

        if (!_overlayState.IsVisible)
            _overlayState.SetVisible(pos.placement);
    }

    /// <summary>F-S06: 즉시 색상 변경 (비트맵 갱신 + premultiply).</summary>
    public static void UpdateColor(ImeState state, AppConfig config)
    {
        _lastRenderedState = null; // 강제 재렌더
        RenderIndicator(state, config);
        UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, _lastAlpha);
    }

    /// <summary>페이드 프레임: SourceConstantAlpha만 변경.</summary>
    public static void UpdateAlpha(byte alpha)
    {
        UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, alpha);
    }

    /// <summary>슬라이드 프레임: 위치만 변경 (비트맵 불변).</summary>
    public static void UpdatePosition(int x, int y)
    {
        _lastX = x;
        _lastY = y;
        UpdateOverlay(x, y, _currentWidth, _currentHeight, _lastAlpha);
    }

    /// <summary>강조 프레임: 중심 기준 확대 (F-S04).</summary>
    public static void UpdateScaledSize(int x, int y, int w, int h, byte alpha)
    {
        UpdateOverlay(x, y, w, h, alpha);
    }

    /// <summary>ShowWindow(SW_HIDE) + 상태 리셋.</summary>
    public static void Hide()
    {
        if (_hwndOverlay != IntPtr.Zero)
            User32.ShowWindow(_hwndOverlay, Win32Constants.SW_HIDE);
        _overlayState.OnFadeOutComplete();
    }

    /// <summary>DPI 변경 시 HFONT/DIB 재생성.</summary>
    public static void HandleDpiChanged(AppConfig config)
    {
        // 캐시 무효화 → EnsureResources에서 재생성
        _cachedFontDpiScale = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        EnsureResources(config);
    }

    /// <summary>설정 변경 시 전체 리소스 리빌드.</summary>
    public static void HandleConfigChanged(AppConfig config)
    {
        _cachedFontFamily = "";
        _cachedFontDpiScale = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        _lastRenderedState = null;
        EnsureResources(config);
    }

    /// <summary>SetWindowPos HWND_TOPMOST 재적용.</summary>
    public static void ForceTopmost()
    {
        if (_hwndOverlay != IntPtr.Zero)
            User32.SetWindowPos(_hwndOverlay, Win32Constants.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE);
    }

    /// <summary>디버그 오버레이 데이터 설정 (감지 스레드에서 호출).</summary>
    public static void SetDebugInfo(DebugInfo? info) => _debugInfo = info;

    /// <summary>강조 스케일 계산용.</summary>
    public static (int w, int h) GetBaseSize() => (_currentWidth, _currentHeight);

    /// <summary>강조 중심점 계산용.</summary>
    public static (int x, int y) GetLastPosition() => (_lastX, _lastY);

    // ================================================================
    // 리소스 관리
    // ================================================================

    private static void EnsureResources(AppConfig config)
    {
        ComputeBaseSize(config);
        int targetW = DpiHelper.Scale(_baseWidth, _currentDpiScale);
        int targetH = DpiHelper.Scale(_baseHeight, _currentDpiScale);

        EnsureFont(config);
        EnsureDib(targetW, targetH);

        if (config.IndicatorStyle == IndicatorStyle.Label)
            CalculateFixedLabelWidth(config);
    }

    private static void ComputeBaseSize(AppConfig config)
    {
        switch (config.IndicatorStyle)
        {
            case IndicatorStyle.Label:
                _baseWidth = config.LabelWidth;
                _baseHeight = config.LabelHeight;
                break;
            case IndicatorStyle.CaretDot:
                _baseWidth = config.CaretDotSize;
                _baseHeight = config.CaretDotSize;
                break;
            case IndicatorStyle.CaretSquare:
                _baseWidth = config.CaretSquareSize;
                _baseHeight = config.CaretSquareSize;
                break;
            case IndicatorStyle.CaretUnderline:
                _baseWidth = config.CaretUnderlineWidth;
                _baseHeight = config.CaretUnderlineHeight;
                break;
            case IndicatorStyle.CaretVbar:
                _baseWidth = config.CaretVbarWidth;
                _baseHeight = config.CaretVbarHeight;
                break;
        }

        // 디버그 오버레이 활성 시 높이 확장 (5줄 x 8pt)
        if (config.Advanced.DebugOverlay)
        {
            _baseWidth = Math.Max(_baseWidth, DebugOverlayWidth);
            _baseHeight += DebugOverlayLineHeight * DebugOverlayLineCount;
        }
    }

    private static void EnsureFont(AppConfig config)
    {
        if (config.FontFamily == _cachedFontFamily
            && config.FontSize == _cachedFontSize
            && config.FontWeight == _cachedFontWeight
            && Math.Abs(_currentDpiScale - _cachedFontDpiScale) < 0.001)
            return;

        _currentFont?.Dispose();

        int fontHeight = -Kernel32.MulDiv(config.FontSize, (int)_currentDpiY, 72);
        IntPtr hFont = Gdi32.CreateFontW(
            fontHeight, 0, 0, 0,
            config.FontWeight == FontWeight.Bold ? Win32Constants.FW_BOLD : Win32Constants.FW_NORMAL,
            0, 0, 0,
            Win32Constants.DEFAULT_CHARSET,
            Win32Constants.OUT_TT_PRECIS,
            Win32Constants.CLIP_DEFAULT_PRECIS,
            Win32Constants.CLEARTYPE_QUALITY,
            Win32Constants.DEFAULT_PITCH,
            config.FontFamily);

        _currentFont = new SafeFontHandle(hFont, true);
        _cachedFontFamily = config.FontFamily;
        _cachedFontSize = config.FontSize;
        _cachedFontWeight = config.FontWeight;
        _cachedFontDpiScale = _currentDpiScale;
    }

    private static void EnsureDib(int width, int height)
    {
        if (width == _currentWidth && height == _currentHeight && _currentBitmap is not null)
            return;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,  // top-down DIB
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32Constants.BI_RGB
        };

        IntPtr hBitmap = Gdi32.CreateDIBSection(
            IntPtr.Zero, ref bmi, Win32Constants.DIB_RGB_COLORS,
            out _ppvBits, IntPtr.Zero, 0);

        if (hBitmap == IntPtr.Zero) return;

        // 새 비트맵 선택 (기존 비트맵 deselect) 후 해제
        Gdi32.SelectObject(_memDC, hBitmap);
        _currentBitmap?.Dispose();
        _currentBitmap = new SafeBitmapHandle(hBitmap, true);

        _currentWidth = width;
        _currentHeight = height;
    }

    private static void CalculateFixedLabelWidth(AppConfig config)
    {
        if (_currentFont is null) return;

        IntPtr oldFont = Gdi32.SelectObject(_memDC, _currentFont.DangerousGetHandle());

        int maxTextWidth = 0;
        string[] labels = [config.HangulLabel, config.EnglishLabel, config.NonKoreanLabel];
        foreach (string label in labels)
        {
            Gdi32.GetTextExtentPoint32W(_memDC, label, label.Length, out SIZE sz);
            if (sz.cx > maxTextWidth) maxTextWidth = sz.cx;
        }

        Gdi32.SelectObject(_memDC, oldFont);

        int padding = 2 * DpiHelper.Scale(DefaultConfig.LABEL_PADDING_X, _currentDpiScale);
        int calculated = maxTextWidth + padding;
        int minWidth = DpiHelper.Scale(config.LabelWidth, _currentDpiScale);
        _fixedLabelWidth = Math.Max(calculated, minWidth);

        // F-S01: 라벨 고정 너비 적용 — DIB 재생성 필요 시
        if (_fixedLabelWidth != _currentWidth)
        {
            int labelH = DpiHelper.Scale(config.LabelHeight, _currentDpiScale);
            EnsureDib(_fixedLabelWidth, labelH);
        }
    }

    // ================================================================
    // 렌더링 파이프라인
    // ================================================================

    private static void RenderIndicator(ImeState state, AppConfig config)
    {
        // 동일 상태면 재렌더 불필요
        if (_lastRenderedState == state) return;
        _lastRenderedState = state;

        int w = _currentWidth;
        int h = _currentHeight;
        if (w == 0 || h == 0) return;

        // 1. 픽셀 버퍼 0 클리어
        unsafe
        {
            new Span<byte>((void*)_ppvBits, w * h * 4).Clear();
        }

        // 2. NULL_PEN 선택 (곡선 도형 테두리 제거)
        IntPtr oldPen = Gdi32.SelectObject(_memDC, _nullPen);

        // 3. 배경색 브러시 생성
        uint bgColor = ColorHelper.HexToColorRef(GetBgHex(state, config));
        IntPtr hBrush = Gdi32.CreateSolidBrush(bgColor);

        // 4~5. 스타일별 도형 그리기
        switch (config.IndicatorStyle)
        {
            case IndicatorStyle.Label:
                RenderLabel(state, config, w, h, hBrush);
                break;
            case IndicatorStyle.CaretDot:
                Gdi32.SelectObject(_memDC, hBrush);
                Gdi32.Ellipse(_memDC, 0, 0, w, h);
                break;
            case IndicatorStyle.CaretSquare:
            {
                var rect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
                User32.FillRect(_memDC, ref rect, hBrush);
                break;
            }
            case IndicatorStyle.CaretUnderline:
            {
                var rect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
                User32.FillRect(_memDC, ref rect, hBrush);
                break;
            }
            case IndicatorStyle.CaretVbar:
            {
                var rect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
                User32.FillRect(_memDC, ref rect, hBrush);
                break;
            }
        }

        // 6. 브러시 삭제 + 펜 복원
        Gdi32.DeleteObject(hBrush);
        Gdi32.SelectObject(_memDC, oldPen);

        // 7. Premultiplied alpha 후처리
        ApplyPremultipliedAlpha(w, h);
    }

    private static void RenderLabel(ImeState state, AppConfig config, int w, int h, IntPtr hBrush)
    {
        // 도형 그리기
        IntPtr oldBrush = Gdi32.SelectObject(_memDC, hBrush);

        switch (config.LabelShape)
        {
            case LabelShape.Circle:
                Gdi32.Ellipse(_memDC, 0, 0, w, h);
                break;
            case LabelShape.Pill:
                Gdi32.RoundRect(_memDC, 0, 0, w, h, h, h);
                break;
            default: // RoundedRect
                int radius = DpiHelper.Scale(config.LabelBorderRadius, _currentDpiScale);
                Gdi32.RoundRect(_memDC, 0, 0, w, h, radius, radius);
                break;
        }

        Gdi32.SelectObject(_memDC, oldBrush);

        // 테두리 렌더링 (border_width > 0 일 때만)
        int borderW = DpiHelper.Scale(config.BorderWidth, _currentDpiScale);
        if (borderW > 0)
        {
            uint borderColor = ColorHelper.HexToColorRef(config.BorderColor);
            IntPtr hBorderPen = Gdi32.CreatePen(Win32Constants.PS_SOLID, borderW, borderColor);
            IntPtr hNullBrush = Gdi32.GetStockObject(Win32Constants.NULL_BRUSH);

            IntPtr oldBorderPen = Gdi32.SelectObject(_memDC, hBorderPen);
            IntPtr oldBorderBrush = Gdi32.SelectObject(_memDC, hNullBrush);

            // 펜 폭 인셋: GDI 펜은 경로 중심에 그려지므로 halfBorder만큼 안쪽으로 오프셋
            int halfBorder = borderW / 2;
            switch (config.LabelShape)
            {
                case LabelShape.Circle:
                    Gdi32.Ellipse(_memDC, halfBorder, halfBorder, w - halfBorder, h - halfBorder);
                    break;
                case LabelShape.Pill:
                    Gdi32.RoundRect(_memDC, halfBorder, halfBorder, w - halfBorder, h - halfBorder, h, h);
                    break;
                default:
                    int borderRadius = DpiHelper.Scale(config.LabelBorderRadius, _currentDpiScale);
                    Gdi32.RoundRect(_memDC, halfBorder, halfBorder, w - halfBorder, h - halfBorder, borderRadius, borderRadius);
                    break;
            }

            Gdi32.SelectObject(_memDC, oldBorderBrush);
            Gdi32.SelectObject(_memDC, oldBorderPen);
            Gdi32.DeleteObject(hBorderPen);
        }

        // 라벨 스타일별 내부 렌더링
        switch (config.LabelStyle)
        {
            case LabelStyle.Dot:
                RenderLabelDot(state, config, w, h);
                break;
            case LabelStyle.Icon:
                RenderLabelIcon(state, config, w, h);
                break;
            default: // Text
                RenderLabelText(state, config, w, h);
                break;
        }
    }

    private static void RenderLabelText(ImeState state, AppConfig config, int w, int h)
    {
        if (_currentFont is null) return;

        IntPtr oldFont = Gdi32.SelectObject(_memDC, _currentFont.DangerousGetHandle());
        int oldBkMode = Gdi32.SetBkMode(_memDC, Win32Constants.TRANSPARENT);
        uint fgColor = ColorHelper.HexToColorRef(GetFgHex(state, config));
        uint oldTextColor = Gdi32.SetTextColor(_memDC, fgColor);

        string labelText = GetLabelText(state, config);
        var textRect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        User32.DrawTextW(_memDC, labelText, labelText.Length, ref textRect,
            Win32Constants.DT_CENTER | Win32Constants.DT_VCENTER | Win32Constants.DT_SINGLELINE);

        Gdi32.SetTextColor(_memDC, oldTextColor);
        Gdi32.SetBkMode(_memDC, oldBkMode);
        Gdi32.SelectObject(_memDC, oldFont);
    }

    private static void RenderLabelDot(ImeState state, AppConfig config, int w, int h)
    {
        // shape 중앙에 foreground 색상 작은 원 (크기: h/3)
        uint fgColor = ColorHelper.HexToColorRef(GetFgHex(state, config));
        IntPtr hDotBrush = Gdi32.CreateSolidBrush(fgColor);
        IntPtr oldBrush = Gdi32.SelectObject(_memDC, hDotBrush);

        int dotSize = Math.Max(h / 3, 2);
        int cx = (w - dotSize) / 2;
        int cy = (h - dotSize) / 2;
        Gdi32.Ellipse(_memDC, cx, cy, cx + dotSize, cy + dotSize);

        Gdi32.SelectObject(_memDC, oldBrush);
        Gdi32.DeleteObject(hDotBrush);
    }

    private static void RenderLabelIcon(ImeState state, AppConfig config, int w, int h)
    {
        if (_currentFont is null) return;

        IntPtr oldFont = Gdi32.SelectObject(_memDC, _currentFont.DangerousGetHandle());
        int oldBkMode = Gdi32.SetBkMode(_memDC, Win32Constants.TRANSPARENT);
        uint fgColor = ColorHelper.HexToColorRef(GetFgHex(state, config));
        uint oldTextColor = Gdi32.SetTextColor(_memDC, fgColor);

        // 한글이면 "ㄱ", 영어/비한국어이면 "A"
        string iconText = state == ImeState.Hangul ? "ㄱ" : "A";
        var textRect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
        User32.DrawTextW(_memDC, iconText, iconText.Length, ref textRect,
            Win32Constants.DT_CENTER | Win32Constants.DT_VCENTER | Win32Constants.DT_SINGLELINE);

        Gdi32.SetTextColor(_memDC, oldTextColor);
        Gdi32.SetBkMode(_memDC, oldBkMode);
        Gdi32.SelectObject(_memDC, oldFont);
    }

    private static unsafe void ApplyPremultipliedAlpha(int w, int h)
    {
        int pixelCount = w * h;
        byte* ptr = (byte*)_ppvBits;

        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            byte b = ptr[offset];
            byte g = ptr[offset + 1];
            byte r = ptr[offset + 2];
            byte a = ptr[offset + 3];

            if (a == 0) continue;
            if (a == 255) continue;  // 완전 불투명은 변환 불필요

            ptr[offset] = (byte)(b * a / 255);
            ptr[offset + 1] = (byte)(g * a / 255);
            ptr[offset + 2] = (byte)(r * a / 255);
        }
    }

    // ================================================================
    // 디버그 오버레이
    // ================================================================

    private static void RenderDebugOverlay(DebugInfo dbg)
    {
        int w = _currentWidth;
        int h = _currentHeight;
        int debugH = DpiHelper.Scale(DebugOverlayLineHeight * DebugOverlayLineCount, _currentDpiScale);
        int debugY = h - debugH;  // 인디케이터 아래 영역

        // 반투명 배경 (어두운 회색)
        uint debugBgColor = ColorHelper.HexToColorRef("#1F1F1F");
        IntPtr hDebugBrush = Gdi32.CreateSolidBrush(debugBgColor);
        var debugRect = new RECT { Left = 0, Top = debugY, Right = w, Bottom = h };
        User32.FillRect(_memDC, ref debugRect, hDebugBrush);
        Gdi32.DeleteObject(hDebugBrush);

        // 소형 폰트 생성 (8pt)
        int debugFontH = -Kernel32.MulDiv(DebugOverlayFontSize, (int)_currentDpiY, 72);
        IntPtr hDebugFont = Gdi32.CreateFontW(
            debugFontH, 0, 0, 0,
            Win32Constants.FW_NORMAL, 0, 0, 0,
            Win32Constants.DEFAULT_CHARSET,
            Win32Constants.OUT_TT_PRECIS,
            Win32Constants.CLIP_DEFAULT_PRECIS,
            Win32Constants.CLEARTYPE_QUALITY,
            Win32Constants.DEFAULT_PITCH,
            "Consolas");

        IntPtr oldFont = Gdi32.SelectObject(_memDC, hDebugFont);
        int oldBkMode = Gdi32.SetBkMode(_memDC, Win32Constants.TRANSPARENT);
        uint oldTextColor = Gdi32.SetTextColor(_memDC, ColorHelper.HexToColorRef("#00FF00"));

        // 5줄 텍스트
        string[] lines =
        [
            $"M:{dbg.Method}",
            $"X:{dbg.CaretX} Y:{dbg.CaretY}",
            $"DPI:{dbg.DpiX}",
            $"Poll:{dbg.PollingMs}ms",
            $"C:{dbg.ClassName}",
        ];

        int lineH = DpiHelper.Scale(DebugOverlayLineHeight, _currentDpiScale);
        for (int i = 0; i < lines.Length; i++)
        {
            var lineRect = new RECT
            {
                Left = 2, Top = debugY + i * lineH,
                Right = w - 2, Bottom = debugY + (i + 1) * lineH,
            };
            User32.DrawTextW(_memDC, lines[i], lines[i].Length, ref lineRect,
                Win32Constants.DT_SINGLELINE);
        }

        // 복원
        Gdi32.SetTextColor(_memDC, oldTextColor);
        Gdi32.SetBkMode(_memDC, oldBkMode);
        Gdi32.SelectObject(_memDC, oldFont);
        Gdi32.DeleteObject(hDebugFont);
    }

    // ================================================================
    // UpdateLayeredWindow 래퍼
    // ================================================================

    private static void UpdateOverlay(int x, int y, int displayW, int displayH, byte alpha)
    {
        _lastAlpha = alpha;
        if (_hwndOverlay == IntPtr.Zero || _memDC == IntPtr.Zero) return;

        var ptDst = new POINT(x, y);
        var size = new SIZE(displayW, displayH);
        var ptSrc = new POINT(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32Constants.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = alpha,
            AlphaFormat = Win32Constants.AC_SRC_ALPHA
        };

        User32.UpdateLayeredWindow(_hwndOverlay, IntPtr.Zero, ref ptDst, ref size,
            _memDC, ref ptSrc, 0, ref blend, Win32Constants.ULW_ALPHA);
    }

    // ================================================================
    // 위치 계산
    // ================================================================

    private static (int x, int y, Placement placement) CalculateIndicatorPosition(
        int caretX, int caretY, int caretH, AppConfig config)
    {
        // 0. position_mode 분기 + 앵커 좌표
        int anchorX, anchorY;
        int effectiveCaretH = caretH;

        switch (config.PositionMode)
        {
            case PositionMode.Mouse:
                User32.GetCursorPos(out POINT pt);
                anchorX = pt.X + config.MouseOffset.X;
                anchorY = pt.Y + config.MouseOffset.Y;
                effectiveCaretH = 0;
                break;
            case PositionMode.Fixed:
                (anchorX, anchorY) = ResolveFixedPosition(config);
                effectiveCaretH = 0;
                break;
            default: // Caret
                anchorX = caretX;
                anchorY = caretY;
                break;
        }

        // 1. 모니터 + DPI 조회
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(anchorX, anchorY);
        double dpiScale = DpiHelper.GetScale(hMonitor);
        (_, uint dpiY) = DpiHelper.GetRawDpi(hMonitor);

        // DPI 캐시 갱신
        if (Math.Abs(dpiScale - _currentDpiScale) > 0.001 || dpiY != _currentDpiY)
        {
            _currentDpiScale = dpiScale;
            _currentDpiY = dpiY;
        }

        // 2. DPI 스케일 적용
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);
        int margin = DpiHelper.Scale(config.ScreenEdgeMargin, dpiScale);
        int sIndW = DpiHelper.Scale(_baseWidth, dpiScale);
        int sIndH = DpiHelper.Scale(_baseHeight, dpiScale);

        // Label은 고정 너비 사용
        if (config.IndicatorStyle == IndicatorStyle.Label && _fixedLabelWidth > 0)
            sIndW = _fixedLabelWidth;

        // caret 모드: CaretOffset DPI 적용
        if (config.PositionMode == PositionMode.Caret)
        {
            anchorX += DpiHelper.Scale(config.CaretOffset.X, dpiScale);
            anchorY += DpiHelper.Scale(config.CaretOffset.Y, dpiScale);
        }

        // 3. 스타일별 좌표 계산
        int x, y;
        Placement placement;

        switch (config.IndicatorStyle)
        {
            case IndicatorStyle.Label:
                (x, y, placement) = CalcLabelPosition(
                    anchorX, anchorY, effectiveCaretH, sIndW, sIndH, margin, workArea, config, dpiScale);
                break;

            case IndicatorStyle.CaretDot:
            case IndicatorStyle.CaretSquare:
                x = anchorX + DpiHelper.Scale(DefaultConfig.CaretBoxGapX, dpiScale);
                y = anchorY - sIndH + DpiHelper.Scale(DefaultConfig.CaretBoxGapY, dpiScale);
                placement = Placement.CaretTopRight;
                break;

            case IndicatorStyle.CaretUnderline:
                x = anchorX - sIndW / 2;
                y = anchorY + effectiveCaretH + DpiHelper.Scale(DefaultConfig.UnderlineGap, dpiScale);
                placement = Placement.CaretBelow;
                break;

            case IndicatorStyle.CaretVbar:
                x = anchorX - DpiHelper.Scale(DefaultConfig.VbarOffsetX, dpiScale);
                y = anchorY;
                placement = Placement.CaretOverlap;
                break;

            default:
                x = anchorX;
                y = anchorY;
                placement = Placement.Left;
                break;
        }

        // 4. rcWork 클램핑
        if (config.ClampToWorkArea)
        {
            x = Math.Max(workArea.Left + margin, Math.Min(x, workArea.Right - sIndW - margin));
            y = Math.Max(workArea.Top + margin, Math.Min(y, workArea.Bottom - sIndH - margin));
        }

        return (x, y, placement);
    }

    private static (int x, int y, Placement placement) CalcLabelPosition(
        int anchorX, int anchorY, int caretH,
        int indW, int indH, int margin, RECT workArea,
        AppConfig config, double dpiScale)
    {
        int gap = DpiHelper.Scale(DefaultConfig.LabelGap, dpiScale);

        // F-S02: 표시 중이면 기존 방향 유지
        if (_overlayState.IsVisible && _overlayState.CurrentPlacement is { } locked)
        {
            (int lx, int ly) = GetLabelCoords(locked, anchorX, anchorY, caretH, indW, indH, gap);
            return (lx, ly, locked);
        }

        // 4방향 fallback 체인
        Placement[] chain = config.CaretPlacement switch
        {
            CaretPlacement.Right => [Placement.Right, Placement.Above, Placement.Below],
            CaretPlacement.Above => [Placement.Above, Placement.Below, Placement.Left],
            CaretPlacement.Below => [Placement.Below, Placement.Above, Placement.Left],
            _ => [Placement.Left, Placement.Above, Placement.Below],  // Left 기본
        };

        if (!config.CaretPlacementAutoFlip)
        {
            // auto-flip 비활성: 1순위만 사용
            (int fx, int fy) = GetLabelCoords(chain[0], anchorX, anchorY, caretH, indW, indH, gap);
            return (fx, fy, chain[0]);
        }

        foreach (Placement dir in chain)
        {
            (int cx, int cy) = GetLabelCoords(dir, anchorX, anchorY, caretH, indW, indH, gap);
            if (IsWithinWorkArea(cx, cy, indW, indH, margin, workArea))
                return (cx, cy, dir);
        }

        // 모든 방향 실패 시 마우스 위치
        User32.GetCursorPos(out POINT mousePt);
        return (mousePt.X, mousePt.Y, Placement.Left);
    }

    private static (int x, int y) GetLabelCoords(
        Placement dir, int anchorX, int anchorY, int caretH,
        int indW, int indH, int gap)
    {
        return dir switch
        {
            Placement.Left => (anchorX - indW - gap, anchorY),
            Placement.Right => (anchorX + gap, anchorY),
            Placement.Above => (anchorX - indW / 2, anchorY - indH - gap),
            Placement.Below => (anchorX - indW / 2, anchorY + caretH + gap),
            _ => (anchorX - indW - gap, anchorY),
        };
    }

    private static bool IsWithinWorkArea(int x, int y, int w, int h, int margin, RECT workArea)
    {
        return x >= workArea.Left + margin
            && y >= workArea.Top + margin
            && x + w <= workArea.Right - margin
            && y + h <= workArea.Bottom - margin;
    }

    // ================================================================
    // Fixed 위치 해석
    // ================================================================

    /// <summary>
    /// anchor + monitor 설정에 따라 Fixed 모드의 스크린 좌표를 계산.
    /// </summary>
    private static (int x, int y) ResolveFixedPosition(AppConfig config)
    {
        var fp = config.FixedPosition;

        // absolute: raw 좌표 그대로
        if (fp.Anchor == FixedAnchor.Absolute)
            return (fp.X, fp.Y);

        // 모니터 영역 조회
        IntPtr hMonitor = ResolveMonitor(fp.Monitor);
        RECT mon = DpiHelper.GetMonitorRect(hMonitor);

        int monW = mon.Right - mon.Left;
        int monH = mon.Bottom - mon.Top;

        // 앵커 기준점에서 X,Y 오프셋 적용
        return fp.Anchor switch
        {
            FixedAnchor.TopLeft => (mon.Left + fp.X, mon.Top + fp.Y),
            FixedAnchor.TopRight => (mon.Right - fp.X, mon.Top + fp.Y),
            FixedAnchor.BottomLeft => (mon.Left + fp.X, mon.Bottom - fp.Y),
            FixedAnchor.BottomRight => (mon.Right - fp.X, mon.Bottom - fp.Y),
            FixedAnchor.Center => (mon.Left + monW / 2 + fp.X, mon.Top + monH / 2 + fp.Y),
            _ => (mon.Left + fp.X, mon.Top + fp.Y),  // fallback → top_left
        };
    }

    /// <summary>
    /// monitor 설정값에 따라 대상 모니터 핸들을 반환.
    /// </summary>
    private static IntPtr ResolveMonitor(FixedMonitor monitor)
    {
        if (monitor == FixedMonitor.Mouse)
        {
            User32.GetCursorPos(out POINT cursor);
            return User32.MonitorFromPoint(cursor, Win32Constants.MONITOR_DEFAULTTONEAREST);
        }

        if (monitor == FixedMonitor.Active)
        {
            IntPtr hwndFg = User32.GetForegroundWindow();
            if (hwndFg != IntPtr.Zero)
                return User32.MonitorFromWindow(hwndFg, Win32Constants.MONITOR_DEFAULTTONEAREST);
        }

        // Primary 또는 알 수 없는 값 → 기본 모니터
        return User32.MonitorFromWindow(IntPtr.Zero, Win32Constants.MONITOR_DEFAULTTOPRIMARY);
    }

    // ================================================================
    // 색상/텍스트 헬퍼
    // ================================================================

    private static string GetBgHex(ImeState state, AppConfig config) => state switch
    {
        ImeState.Hangul => config.HangulBg,
        ImeState.English => config.EnglishBg,
        _ => config.NonKoreanBg,
    };

    private static string GetFgHex(ImeState state, AppConfig config) => state switch
    {
        ImeState.Hangul => config.HangulFg,
        ImeState.English => config.EnglishFg,
        _ => config.NonKoreanFg,
    };

    private static string GetLabelText(ImeState state, AppConfig config) => state switch
    {
        ImeState.Hangul => config.HangulLabel,
        ImeState.English => config.EnglishLabel,
        _ => config.NonKoreanLabel,
    };

    // ================================================================
    // OverlayState (배치 방향 + 가시성 추적)
    // ================================================================

    private sealed class OverlayState
    {
        public Placement? CurrentPlacement { get; private set; }
        public bool IsVisible { get; private set; }

        /// <summary>F-S02: IsVisible이 true인 동안 CurrentPlacement 변경 불가.</summary>
        public void SetVisible(Placement placement)
        {
            if (!IsVisible)
            {
                CurrentPlacement = placement;
                IsVisible = true;
            }
        }

        public void OnFadeOutComplete()
        {
            CurrentPlacement = null;
            IsVisible = false;
        }
    }
}
