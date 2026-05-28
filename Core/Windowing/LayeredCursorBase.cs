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
/// ~120 LOC 중 PR-18 의 <see cref="DibSectionFactory"/> + <see cref="LayeredWindowBlit"/> 로
/// ~50 LOC 가 공유되었고, 남은 ~40 LOC <see cref="ApplyPremultipliedAlpha"/> 는 의미 차이
/// (overlay = AA 엣지 보존 / cursor = 외곽 잡티 제거) 로 의도적 분기 보존.
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
    // cursor 인디는 페이드 없이 항상 alpha=255 로 표시. 메인 인디의 OverlayAnimator alpha 보간과 무관.
    // 0 으로 두면 첫 Render 의 UpdateLayeredWindow 가 SourceConstantAlpha=0 (완전 투명) 으로 그려져
    // 사용자가 못 봄.
    private byte _lastAlpha = 255;

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

    /// <summary>
    /// cursor 가 위치한 모니터의 DPI 를 사전 계산해 정확한 bbox 중심 좌표로 위치 캐시 세팅.
    /// <para>
    /// <see cref="Show(int, int)"/> 는 좌상단 좌표만 받아 호출자가 halfBbox 를 미리 계산해야 했는데,
    /// 호출자가 직전 DIB 크기 (이전 모니터 DPI 기준) 로 halfBbox 계산 → DPI 다른 모니터 진입 시
    /// 한 프레임 위치 race + 좌표 오차 발생. 본 메서드는 (1) 새 DPI 갱신, (2) style.BoundingBoxLogicalPx
    /// 를 새 DPI 로 스케일, (3) cursor center 기준 좌상단 = (centerX - bbox/2, centerY - bbox/2) 직접
    /// 계산 — race 없이 정확한 중심 보장.
    /// </para>
    /// </summary>
    public void ShowAtCenter(int centerX, int centerY, CursorStyle style)
    {
        UpdateDpiFromPoint(centerX, centerY);
        int targetSize = DpiHelper.Scale(style.BoundingBoxLogicalPx, _currentDpiScale);
        int halfBbox = targetSize / 2;
        _lastX = centerX - halfBbox;
        _lastY = centerY - halfBbox;
        _isVisible = true;
    }

    /// <summary>
    /// 윈도우는 항상 WS_VISIBLE 상태 유지 (CreateCursorOverlayWindow 에서 박음) — Hide 시 SW_HIDE 가
    /// 아닌 SourceConstantAlpha=0 으로 UpdateLayeredWindow 호출해 시각만 완전 투명. z-order 변경 0
    /// → 트레이 메뉴 modal loop 안에서 호출되어도 메뉴 dismiss 트리거 안 함.
    /// <para>
    /// <c>_lastAlpha</c> 는 255 로 복원 — 다음 Render 의 UpdateOverlay 가 alpha=255 로 정상 표시.
    /// (UpdateOverlay 가 _lastAlpha 캐시 갱신하므로 별도 복원 필요.)
    /// </para>
    /// </summary>
    public void Hide()
    {
        if (_hwnd != IntPtr.Zero && _currentWidth > 0)
        {
            UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, 0);
            _lastAlpha = 255;
        }
        _isVisible = false;
    }

    /// <summary>
    /// DIB 사전 생성 + alpha=0 으로 첫 UpdateLayeredWindow 호출 — WS_VISIBLE 윈도우의 비트맵 캐시
    /// 보장 (dev-notes/2026-05-20 가설 A: Render 전 visible → 비트맵 없이 캐싱 → 후속 UpdateLayeredWindow
    /// 안 그려짐 회피). 시각적으론 완전 투명 (alpha=0). Hide 와 동일하게 _lastAlpha 는 255 복원.
    /// </summary>
    public void PrepareResources(CursorStyle style)
    {
        EnsureDib(style);
        if (_currentWidth > 0)
        {
            UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, 0);
            _lastAlpha = 255;
        }
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

        if (!DibSectionFactory.TryCreate(_memDC, targetSize, targetSize, out var bitmap, out var ppvBits))
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
        _currentBitmap?.Dispose();
        _currentBitmap = bitmap;
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
                // cursor 인디 셰이더는 alpha 를 명시적으로 쓰므로 a==0 && RGB!=0 픽셀은 ShadeDib 의
                // round-down 부산물 (avgAlpha × 255 < 0.5). 메인 LayeredOverlayBase 의 동명 가드는
                // GDI AA 엣지 보존 목적으로 a=255 복구하지만, cursor 인디에선 그 픽셀이 fully-opaque
                // 점이 되어 외곽 잡티의 원인 — RGB 까지 0 으로 정리한다.
                if ((r | g | b) != 0)
                {
                    ptr[offset] = 0;
                    ptr[offset + 1] = 0;
                    ptr[offset + 2] = 0;
                }
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

        LayeredWindowBlit.Blit(_hwnd, _memDC, x, y, displayW, displayH, alpha);
    }
}
