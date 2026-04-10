using System.Runtime.InteropServices;
using KoEnVue.Config;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.UI;

/// <summary>
/// GDI 기반 오버레이 렌더링 + 드래그 가능한 플로팅 인디케이터.
/// 앱별 위치 기억. 모든 호출은 메인 스레드에서만 수행.
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
    private static bool _isVisible;
    private static ImeState? _lastRenderedState;
    private static int _lastX;
    private static int _lastY;
    private static byte _lastAlpha;

    // 드래그 상태
    private static bool _isDragging;

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
    /// 지정 좌표에 인디케이터 렌더 + UpdateLayeredWindow.
    /// 좌표가 속한 모니터 DPI로 리소스 갱신.
    /// </summary>
    public static void Show(int x, int y, ImeState state, AppConfig config)
    {
        // 위치 기준 모니터 DPI 갱신
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x, y);
        (uint dpiX, uint dpiY) = DpiHelper.GetRawDpi(hMonitor);
        double dpiScale = dpiX / (double)DpiHelper.BASE_DPI;

        // DPI 변경 시 캐시 리셋 (모니터 이동 또는 초기화 후 첫 Show)
        if (Math.Abs(dpiScale - _currentDpiScale) > 0.001)
        {
            _fixedLabelWidth = 0;
            _currentWidth = 0;
            _currentHeight = 0;
            _cachedFontDpiScale = 0;
        }

        _currentDpiScale = dpiScale;
        _currentDpiY = dpiY;

        EnsureResources(config);
        RenderIndicator(state, config);
        UpdateOverlay(x, y, _currentWidth, _currentHeight, _lastAlpha);
        _lastX = x;
        _lastY = y;
        _isVisible = true;
    }

    /// <summary>즉시 색상 변경 (비트맵 갱신 + premultiply).</summary>
    public static void UpdateColor(ImeState state, AppConfig config)
    {
        _lastRenderedState = null;
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

    /// <summary>강조 프레임: 중심 기준 확대.</summary>
    public static void UpdateScaledSize(int x, int y, int w, int h, byte alpha)
    {
        UpdateOverlay(x, y, w, h, alpha);
    }

    /// <summary>ShowWindow(SW_HIDE) + 상태 리셋.</summary>
    public static void Hide()
    {
        if (_hwndOverlay != IntPtr.Zero)
            User32.ShowWindow(_hwndOverlay, Win32Constants.SW_HIDE);
        _isVisible = false;
    }

    /// <summary>DPI 변경 시 HFONT/DIB 재생성.</summary>
    public static void HandleDpiChanged(AppConfig config)
    {
        _cachedFontDpiScale = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        _fixedLabelWidth = 0;
        EnsureResources(config);
    }

    /// <summary>설정 변경 시 전체 리소스 리빌드.</summary>
    public static void HandleConfigChanged(AppConfig config)
    {
        _cachedFontFamily = "";
        _cachedFontDpiScale = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        _fixedLabelWidth = 0;
        _lastRenderedState = null;
        EnsureResources(config);
    }

    /// <summary>드래그 시작 (WM_ENTERSIZEMOVE). UpdateOverlay 억제.</summary>
    public static void BeginDrag() => _isDragging = true;

    /// <summary>
    /// 드래그 중 모니터 변경 시 DPI 재계산 + 리렌더 (WM_MOVING).
    /// 시스템 이동 루프가 위치를 제어하므로 제안 위치 기준으로 모니터 판별.
    /// </summary>
    public static void HandleDragDpiChange(int x, int y, ImeState state, AppConfig config)
    {
        if (!_isDragging) return;

        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x, y);
        (uint dpiX, uint dpiY) = DpiHelper.GetRawDpi(hMonitor);
        double dpiScale = dpiX / (double)DpiHelper.BASE_DPI;

        if (Math.Abs(dpiScale - _currentDpiScale) < 0.001) return;

        // DPI 캐시 리셋 + 리소스 재생성
        _fixedLabelWidth = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        _cachedFontDpiScale = 0;
        _currentDpiScale = dpiScale;
        _currentDpiY = dpiY;

        EnsureResources(config);
        RenderIndicator(state, config);

        // _isDragging 가드를 우회하여 UpdateLayeredWindow 직접 호출
        var ptDst = new POINT(x, y);
        var size = new SIZE(_currentWidth, _currentHeight);
        var ptSrc = new POINT(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32Constants.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = _lastAlpha,
            AlphaFormat = Win32Constants.AC_SRC_ALPHA
        };

        User32.UpdateLayeredWindow(_hwndOverlay, IntPtr.Zero, ref ptDst, ref size,
            _memDC, ref ptSrc, 0, ref blend, Win32Constants.ULW_ALPHA);
    }

    /// <summary>드래그 종료 (WM_EXITSIZEMOVE). 새 위치를 _lastX/_lastY에 반영.</summary>
    public static (int x, int y) EndDrag()
    {
        _isDragging = false;
        if (_hwndOverlay != IntPtr.Zero)
        {
            User32.GetWindowRect(_hwndOverlay, out RECT rc);
            _lastX = rc.Left;
            _lastY = rc.Top;
        }
        return (_lastX, _lastY);
    }

    /// <summary>시스템 입력 프로세스 기본 위치의 창 상단 여백(px). 라벨 아래쪽 끝과 창 위쪽 끝 사이 간격.</summary>
    private const int SystemInputGapPx = 4;

    /// <summary>
    /// 앱별 저장 위치가 없을 때 기본 위치.
    /// - 일반 프로세스: 포그라운드 창이 있는 모니터 우상단.
    /// - 시스템 입력 프로세스(시작 메뉴, 검색 창): 포그라운드 창의 시각적 왼쪽 위 모서리 바로 위쪽.
    ///   창 rect는 DWM extended frame bounds로 받아 invisible resize border를 배제한다.
    /// </summary>
    public static (int x, int y) GetDefaultPosition(IntPtr hwndForeground, string processName)
    {
        IntPtr hMonitor = (hwndForeground != IntPtr.Zero)
            ? User32.MonitorFromWindow(hwndForeground, Win32Constants.MONITOR_DEFAULTTOPRIMARY)
            : User32.MonitorFromWindow(_hwndOverlay, Win32Constants.MONITOR_DEFAULTTOPRIMARY);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);

        if (DefaultConfig.IsSystemInputProcess(processName)
            && hwndForeground != IntPtr.Zero
            && Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT frame))
        {
            int labelH = _currentHeight > 0 ? _currentHeight : DpiHelper.Scale(_baseHeight, _currentDpiScale);
            int x = frame.Left;
            int y = frame.Top - labelH - SystemInputGapPx;
            if (y < workArea.Top) y = workArea.Top;
            return (x, y);
        }

        int defaultX = workArea.Right - 200;
        int defaultY = workArea.Top + 10;
        return (defaultX, defaultY);
    }

    /// <summary>SetWindowPos HWND_TOPMOST 재적용.</summary>
    public static void ForceTopmost()
    {
        if (_hwndOverlay != IntPtr.Zero)
            User32.SetWindowPos(_hwndOverlay, Win32Constants.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE);
    }

    /// <summary>강조 스케일 계산용.</summary>
    public static (int w, int h) GetBaseSize() => (_currentWidth, _currentHeight);

    /// <summary>강조 중심점 계산용.</summary>
    public static (int x, int y) GetLastPosition() => (_lastX, _lastY);

    /// <summary>현재 가시성.</summary>
    public static bool IsVisible => _isVisible;

    // ================================================================
    // 리소스 관리
    // ================================================================

    private static void EnsureResources(AppConfig config)
    {
        _baseWidth = config.LabelWidth;
        _baseHeight = config.LabelHeight;

        int targetW = DpiHelper.Scale(_baseWidth, _currentDpiScale);
        int targetH = DpiHelper.Scale(_baseHeight, _currentDpiScale);

        EnsureFont(config);

        if (_fixedLabelWidth > 0)
            targetW = _fixedLabelWidth;

        EnsureDib(targetW, targetH);
        CalculateFixedLabelWidth(config);
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

        Gdi32.SelectObject(_memDC, hBitmap);
        _currentBitmap?.Dispose();
        _currentBitmap = new SafeBitmapHandle(hBitmap, true);

        _currentWidth = width;
        _currentHeight = height;
        _lastRenderedState = null;
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

        if (_fixedLabelWidth != _currentWidth)
        {
            int labelH = DpiHelper.Scale(config.LabelHeight, _currentDpiScale);
            EnsureDib(_fixedLabelWidth, labelH);
        }
    }

    // ================================================================
    // 렌더링
    // ================================================================

    private static void RenderIndicator(ImeState state, AppConfig config)
    {
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

        // 2. NULL_PEN 선택
        IntPtr oldPen = Gdi32.SelectObject(_memDC, _nullPen);

        // 3. 배경색 브러시
        uint bgColor = ColorHelper.HexToColorRef(GetBgHex(state, config));
        IntPtr hBrush = Gdi32.CreateSolidBrush(bgColor);

        // 4. RoundedRect 배경
        IntPtr oldBrush = Gdi32.SelectObject(_memDC, hBrush);
        int radius = DpiHelper.Scale(config.LabelBorderRadius, _currentDpiScale);
        Gdi32.RoundRect(_memDC, 0, 0, w, h, radius, radius);
        Gdi32.SelectObject(_memDC, oldBrush);

        // 5. 테두리 (border_width > 0)
        int borderW = DpiHelper.Scale(config.BorderWidth, _currentDpiScale);
        if (borderW > 0)
        {
            uint borderColor = ColorHelper.HexToColorRef(config.BorderColor);
            IntPtr hBorderPen = Gdi32.CreatePen(Win32Constants.PS_SOLID, borderW, borderColor);
            IntPtr hNullBrush = Gdi32.GetStockObject(Win32Constants.NULL_BRUSH);

            IntPtr oldBorderPen = Gdi32.SelectObject(_memDC, hBorderPen);
            IntPtr oldBorderBrush = Gdi32.SelectObject(_memDC, hNullBrush);

            int halfBorder = borderW / 2;
            int borderRadius = DpiHelper.Scale(config.LabelBorderRadius, _currentDpiScale);
            Gdi32.RoundRect(_memDC, halfBorder, halfBorder, w - halfBorder, h - halfBorder, borderRadius, borderRadius);

            Gdi32.SelectObject(_memDC, oldBorderBrush);
            Gdi32.SelectObject(_memDC, oldBorderPen);
            Gdi32.DeleteObject(hBorderPen);
        }

        // 6. 텍스트 렌더링
        RenderLabelText(state, config, w, h);

        // 7. 정리
        Gdi32.DeleteObject(hBrush);
        Gdi32.SelectObject(_memDC, oldPen);

        // 8. Premultiplied alpha 후처리
        ApplyPremultipliedAlpha(w, h);
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

            if (a == 0)
            {
                if ((r | g | b) != 0)
                    ptr[offset + 3] = 255;
                continue;
            }

            if (a == 255) continue;

            ptr[offset] = (byte)(b * a / 255);
            ptr[offset + 1] = (byte)(g * a / 255);
            ptr[offset + 2] = (byte)(r * a / 255);
        }
    }

    // ================================================================
    // UpdateLayeredWindow 래퍼
    // ================================================================

    private static void UpdateOverlay(int x, int y, int displayW, int displayH, byte alpha)
    {
        _lastAlpha = alpha;
        if (_hwndOverlay == IntPtr.Zero || _memDC == IntPtr.Zero) return;
        if (_isDragging) return;  // 드래그 중 위치 충돌 방지

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
}
