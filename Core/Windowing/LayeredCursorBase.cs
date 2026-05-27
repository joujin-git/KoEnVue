using System.Runtime.InteropServices;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// 커서 추종 인디케이터 (동심원 3개 + 헤일로) 의 레이어드 오버레이 렌더 엔진.
/// <see cref="LayeredOverlayBase"/> 와 형제 — 메인 인디는 GDI 텍스트/RoundRect/폰트/드래그/스냅
/// 책임이 있는 반면, 본 엔진은 분석적 AA 픽셀 셰이딩 (콜백에 ppvBits 직접 전달) 하나만 책임진다.
/// <para>
/// P4 ("하나의 구현만") 예외 정당화는 [docs/dev-notes/2026-05-27-cursor-indicator.md](../../docs/dev-notes/2026-05-27-cursor-indicator.md):
/// 메인 인디 알파 race 미해결 영역에 변경면을 추가하지 않기 위한 의도적 분리. 공유 가능한
/// ~120 LOC (DIB 생성 / premultiply / UpdateLayeredWindow) 가 중복되나, 회귀 차단 가치가 우선.
/// </para>
/// <para>
/// 콜백 시그니처 — 메인 인디의 콜백 (hdc 받음) 과 다르게 ppvBits 를 직접 받는다. cursor 인디는
/// GDI 그리기 (DrawTextW / RoundRect) 사용 없이 픽셀 셰이딩만 수행하므로 GetCurrentObject +
/// GetObjectDibSection 호출이 불필요 → main 의 Gdi32.cs 변경 0.
/// </para>
/// <para>
/// 모든 메서드는 단일 스레드 (메인 UI) 에서만 호출.
/// </para>
/// </summary>
internal sealed class LayeredCursorBase : IDisposable
{
    private const double DpiScaleTolerance = 0.001;

    private readonly IntPtr _hwnd;
    private readonly Func<IntPtr, CursorStyle, CursorMetrics, (int w, int h)> _renderToDib;

    private IntPtr _memDC;
    private SafeBitmapHandle? _currentBitmap;
    private IntPtr _ppvBits;

    private double _currentDpiScale = 1.0;

    private int _currentWidth;
    private int _currentHeight;

    private bool _dibFailureLogged;

    private bool _isVisible;
    private CursorStyle? _lastRenderedStyle;
    private int _lastX;
    private int _lastY;
    private byte _lastAlpha;

    public LayeredCursorBase(
        IntPtr hwnd,
        Func<IntPtr, CursorStyle, CursorMetrics, (int w, int h)> renderToDib)
    {
        _hwnd = hwnd;
        _renderToDib = renderToDib;
        _memDC = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        if (_memDC == IntPtr.Zero)
            throw new InvalidOperationException("CreateCompatibleDC failed");
    }

    public void Dispose()
    {
        _currentBitmap?.Dispose();
        _currentBitmap = null;
        if (_memDC != IntPtr.Zero)
        {
            Gdi32.DeleteDC(_memDC);
            _memDC = IntPtr.Zero;
        }
    }

    public IntPtr Hwnd => _hwnd;
    public bool IsVisible => _isVisible;
    public (int w, int h) GetBaseSize() => (_currentWidth, _currentHeight);
    public (int x, int y) GetLastPosition() => (_lastX, _lastY);

    /// <summary>
    /// 지정 좌표 기준 모니터 DPI 갱신 + 위치 캐시 업데이트. 블리트 안 함 — 후속 <see cref="Render"/>
    /// 가 DIB 그리기 + UpdateLayeredWindow 수행.
    /// </summary>
    public void Show(int x, int y)
    {
        UpdateDpiFromPoint(x, y);
        _lastX = x;
        _lastY = y;
        _isVisible = true;
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
            User32.ShowWindow(_hwnd, Win32Constants.SW_HIDE);
        _isVisible = false;
    }

    /// <summary>
    /// DIB 사전 생성 — 첫 Render 없이도 GetBaseSize 가 유의미한 값을 반환. 그리기/블리트 안 함.
    /// </summary>
    public void PrepareResources(CursorStyle style)
    {
        EnsureDib(style);
    }

    /// <summary>
    /// CursorStyle 에 따라 DIB 를 (재) 생성하고 콜백을 호출해 픽셀 셰이딩 + premultiply +
    /// UpdateLayeredWindow. flip-flop 가드 — 동일 스타일이면 DIB 재생성 스킵 + 블리트만.
    /// </summary>
    public void Render(CursorStyle style)
    {
        if (_lastRenderedStyle is CursorStyle prev && prev == style)
        {
            UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, _lastAlpha);
            return;
        }

        if (!PaintDib(style)) return;
        UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, _lastAlpha);
    }

    public void UpdateAlpha(byte alpha)
    {
        UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, alpha);
    }

    public void UpdatePosition(int x, int y)
    {
        _lastX = x;
        _lastY = y;
        UpdateOverlay(x, y, _currentWidth, _currentHeight, _lastAlpha);
    }

    /// <summary>
    /// DPI 변경 시 캐시 무효화. 다음 <see cref="Render"/> 호출이 DIB 를 재생성.
    /// </summary>
    public void HandleDpiChanged()
    {
        _currentWidth = 0;
        _currentHeight = 0;
        _lastRenderedStyle = null;
    }

    private bool PaintDib(CursorStyle style)
    {
        EnsureDib(style);

        int w = _currentWidth;
        int h = _currentHeight;
        if (w == 0 || h == 0) return false;
        if (_ppvBits == IntPtr.Zero) return false;

        unsafe
        {
            new Span<byte>((void*)_ppvBits, w * h * 4).Clear();
        }

        var metrics = new CursorMetrics(_currentDpiScale, w, h);
        _renderToDib(_ppvBits, style, metrics);

        ApplyPremultipliedAlpha(w, h);
        _lastRenderedStyle = style;
        return true;
    }

    private void UpdateDpiFromPoint(int x, int y)
    {
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x, y);
        (uint dpiX, uint _) = DpiHelper.GetRawDpi(hMonitor);
        double dpiScale = dpiX / (double)DpiHelper.BASE_DPI;

        if (Math.Abs(dpiScale - _currentDpiScale) > DpiScaleTolerance)
        {
            _currentWidth = 0;
            _currentHeight = 0;
            _lastRenderedStyle = null;
        }

        _currentDpiScale = dpiScale;
    }

    private void EnsureDib(CursorStyle style)
    {
        int targetSize = DpiHelper.Scale(style.BoundingBoxLogicalPx, _currentDpiScale);
        if (targetSize == _currentWidth && targetSize == _currentHeight && _currentBitmap is not null)
            return;

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = targetSize,
            biHeight = -targetSize,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Win32Constants.BI_RGB
        };

        IntPtr hBitmap = Gdi32.CreateDIBSection(
            IntPtr.Zero, ref bmi, Win32Constants.DIB_RGB_COLORS,
            out IntPtr ppvBits, IntPtr.Zero, 0);

        if (hBitmap == IntPtr.Zero)
        {
            if (!_dibFailureLogged)
            {
                _dibFailureLogged = true;
                LogProvider.Sink?.Warning($"CreateDIBSection failed (cursor): {targetSize}x{targetSize}. Keeping previous DIB.");
            }
            return;
        }

        _dibFailureLogged = false;
        _ppvBits = ppvBits;

        Gdi32.SelectObject(_memDC, hBitmap);
        _currentBitmap?.Dispose();
        _currentBitmap = new SafeBitmapHandle(hBitmap, true);

        _currentWidth = targetSize;
        _currentHeight = targetSize;
        _lastRenderedStyle = null;
    }

    private unsafe void ApplyPremultipliedAlpha(int w, int h)
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

    private void UpdateOverlay(int x, int y, int displayW, int displayH, byte alpha)
    {
        _lastAlpha = alpha;
        if (_hwnd == IntPtr.Zero || _memDC == IntPtr.Zero) return;

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

        User32.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref ptDst, ref size,
            _memDC, ref ptSrc, 0, ref blend, Win32Constants.ULW_ALPHA);
    }
}
