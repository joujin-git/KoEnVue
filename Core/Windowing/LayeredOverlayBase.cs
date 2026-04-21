using System.Runtime.InteropServices;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// GDI + UpdateLayeredWindow 기반 레이어드 오버레이 렌더 엔진.
/// <para>
/// 앱별 의존성(설정 레코드, 상태 enum)을 전혀 포함하지 않으며 파사드가 합성한
/// <see cref="OverlayStyle"/>만 받는다. 실제 GDI 그리기(RoundRect, DrawTextW 등)는
/// 생성자에 주입된 <c>renderToDib</c> 콜백이 담당하고, 엔진은 DIB/HFONT 리소스 생명주기,
/// 드래그 처리, 창 엣지 스냅, DPI 전환, premultiplied alpha 후처리, UpdateLayeredWindow
/// 호출을 책임진다.
/// </para>
/// <para>
/// 모든 메서드는 단일 스레드(메인 UI)에서만 호출되어야 한다. EnumWindows 콜백에서 사용하는
/// 스냅 후보 리스트와 overlay hwnd는 앱 당 하나의 엔진 인스턴스만 존재한다는 가정 하에
/// 정적 필드로 관리된다.
/// </para>
/// </summary>
internal sealed class LayeredOverlayBase : IDisposable
{
    // ================================================================
    // 상수
    // ================================================================

    // DPI 스케일 비교용 허용 오차. 부동소수 double 이므로 exact 비교 대신
    // |a-b| < tolerance 로 "사실상 같은 스케일" 을 판정한다.
    private const double DpiScaleTolerance = 0.001;

    // ================================================================
    // GDI 리소스 상태
    // ================================================================

    private readonly IntPtr _hwndOverlay;
    private readonly Func<IntPtr, OverlayStyle, OverlayMetrics, (int w, int h)> _renderToDib;

    private IntPtr _memDC;
    private SafeBitmapHandle? _currentBitmap;
    private SafeFontHandle? _currentFont;
    private IntPtr _ppvBits;

    // DPI 캐시
    private double _currentDpiScale = 1.0;
    private uint _currentDpiY = DpiHelper.BASE_DPI;

    // DIB 크기 캐시
    private int _currentWidth;
    private int _currentHeight;

    // 기본 크기 (DPI 미적용)
    private int _baseWidth;
    private int _baseHeight;

    // F-S01: 라벨 고정 너비
    private int _fixedLabelWidth;

    // 폰트 캐시 키
    private string _cachedFontFamily = "";
    private int _cachedFontSize;
    private bool _cachedFontIsBold;
    private double _cachedFontDpiScale;

    // 라벨 폭 계산 캐시 키 — (레이블 3종 + 폰트 서명 + DPI + 패딩 + 최소폭) 이 불변이면
    // GetTextExtentPoint32W 3회 호출을 생략한다. 각 EnsureResources 진입마다 같은 키로
    // 재진입하는 것이 일반적이므로 체감 이득은 크다.
    private (string Hangul, string English, string NonKorean) _cachedLabelMeasureKey;
    private int _cachedLabelPaddingLogicalPx = -1;
    private int _cachedLabelMinWidthLogicalPx = -1;
    private double _cachedLabelDpiScale;
    private string _cachedLabelFontFamily = "";
    private int _cachedLabelFontSize;
    private bool _cachedLabelFontIsBold;

    // CreateDIBSection 실패 시 Logger.Warning 을 한 번만 남기기 위한 래치.
    // 빠른 IME 토글/DPI 전환 시 매 호출마다 기록되면 로그 스팸이 되므로 초회 1회만 남기고
    // 이후 실패는 조용히 fall-through — 기존 _ppvBits / _currentBitmap 은 보존된다.
    private bool _dibFailureLogged;

    // DT_VCENTER가 셀 중앙(tmAscent+tmDescent의 중점)을 기준으로 정렬하기 때문에 발생하는
    // 시각적 하향 치우침 보정값. 양수일수록 텍스트를 위로 끌어올린다(픽셀 단위).
    // EnsureFont에서 GetTextMetricsW로 한 번 측정해 캐시하며, 폰트 캐시 키가 무효화될 때만
    // 재측정된다. → 부팅 1회 + 폰트/크기/굵기/DPI 변경 시점에만 호출.
    private int _textVCenterOffsetPx;

    // 위치/상태 추적
    private bool _isVisible;
    private OverlayStyle? _lastRenderedStyle;
    private int _lastX;
    private int _lastY;
    private byte _lastAlpha;

    // 드래그 상태
    private bool _isDragging;
    private int _dragStartX;
    private int _dragStartY;
    // 드래그 시작 시 커서가 창 안에서 잡고 있는 상대 오프셋(hot point).
    // HandleMoving에서 매 틱 (커서 - hot point)로 rect를 재동기화하기 위한 기준.
    // WM_MOVING이 우리 수정값을 새 base로 삼아 다음 틱 proposed를 계산하는 탓에
    // 스냅 델타가 누적되어 창과 커서가 벌어지는 현상을 방지한다.
    private int _dragHotPointX;
    private int _dragHotPointY;

    // ================================================================
    // EnumWindows 콜백용 정적 브리지 (인스턴스 하나 가정)
    //
    // [UnmanagedCallersOnly] 콜백은 인스턴스 필드에 접근할 수 없어 정적 필드로 브리지한다.
    // BeginDrag 시점에 s_activeSnapRects / s_activeOwnerHwnd에 현재 드래그 중인 엔진의
    // 스냅 캐시를 바인딩하고, EndDrag에서 클리어한다.
    // ================================================================

    private static readonly List<RECT> s_activeSnapRects = new(64);
    private static IntPtr s_activeOwnerHwnd;

    // ================================================================
    // 생성자 / 해제
    // ================================================================

    public LayeredOverlayBase(
        IntPtr hwnd,
        Func<IntPtr, OverlayStyle, OverlayMetrics, (int w, int h)> renderToDib)
    {
        _hwndOverlay = hwnd;
        _renderToDib = renderToDib;
        _memDC = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        if (_memDC == IntPtr.Zero)
            throw new InvalidOperationException("CreateCompatibleDC failed");
    }

    public void Dispose()
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
    // Public 조회
    // ================================================================

    /// <summary>오버레이 윈도우 핸들. 파사드가 GetDefaultPosition 등에서 사용.</summary>
    public IntPtr Hwnd => _hwndOverlay;

    /// <summary>현재 가시성.</summary>
    public bool IsVisible => _isVisible;

    /// <summary>현재 DIB 물리 픽셀 크기. 첫 Render 이전에는 (0,0).</summary>
    public (int w, int h) GetBaseSize() => (_currentWidth, _currentHeight);

    /// <summary>마지막 표시 좌표.</summary>
    public (int x, int y) GetLastPosition() => (_lastX, _lastY);

    // ================================================================
    // Public 상태 변경
    // ================================================================

    /// <summary>
    /// 지정 좌표 기준 모니터 DPI로 리소스 갱신 + 위치 캐시 업데이트.
    /// 이 메서드는 블리트를 수행하지 않는다 — 후속 <see cref="Render"/> 호출이 DIB를 그리고
    /// UpdateLayeredWindow를 수행한다.
    /// </summary>
    public void Show(int x, int y)
    {
        UpdateDpiFromPoint(x, y);
        _lastX = x;
        _lastY = y;
        _isVisible = true;
    }

    /// <summary>ShowWindow(SW_HIDE) + 가시 상태 리셋.</summary>
    public void Hide()
    {
        if (_hwndOverlay != IntPtr.Zero)
            User32.ShowWindow(_hwndOverlay, Win32Constants.SW_HIDE);
        _isVisible = false;
    }

    /// <summary>
    /// DIB/HFONT 리소스를 사전 생성한다 (첫 Render 없이도 GetBaseSize가 유의미한 값을
    /// 반환하도록). 파사드 Initialize가 호출하는 진입점. 그리기/블리트 없음.
    /// </summary>
    public void PrepareResources(OverlayStyle style)
    {
        EnsureResources(style);
        CalculateFixedLabelWidth(style);
    }

    /// <summary>
    /// OverlayStyle에 따라 DIB/HFONT 리소스를 (재)생성하고 파사드 콜백을 호출해
    /// DIB 버퍼에 그린 뒤 premultiplied alpha 후처리 + UpdateLayeredWindow.
    /// 이전 스타일과 값 동등성 비교 — 동일하면 DIB 재생성은 스킵하되 UpdateLayeredWindow는
    /// 수행한다 (flip-flop 가드는 그리기 비용만 절감하며, 블리트는 위치 변경 반영을 위해 필요).
    /// </summary>
    public void Render(OverlayStyle style)
    {
        // Flip-flop 가드: 스타일이 동일하면 DIB/HFONT 재생성을 스킵하고 기존 픽셀로 블리트만 수행.
        // 원본 Overlay.Show의 "RenderIndicator (내부에서 early return) → UpdateOverlay 항상 호출"
        // 시퀀스를 보존하기 위해 위치/알파는 항상 반영되어야 한다.
        if (_lastRenderedStyle is OverlayStyle prev && prev == style)
        {
            UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, _lastAlpha);
            return;
        }

        if (!PaintDib(style)) return;
        UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, _lastAlpha);
    }

    /// <summary>
    /// EnsureResources + CalculateFixedLabelWidth + DIB 픽셀 클리어 + 폰트 SelectObject +
    /// 파사드 콜백 호출 + 폰트 복원 + premultiplied alpha 후처리를 원자적으로 수행한다.
    /// <c>_lastRenderedStyle</c>은 성공 시 갱신. 리소스 초기화 실패(w/h = 0)는 false 반환.
    /// Render와 HandleDragDpiChange에서 공유.
    /// </summary>
    private bool PaintDib(OverlayStyle style)
    {
        EnsureResources(style);
        CalculateFixedLabelWidth(style);

        int w = _currentWidth;
        int h = _currentHeight;
        if (w == 0 || h == 0) return false;
        // EnsureDib 가 CreateDIBSection 실패 후 옛 _currentBitmap 을 유지하는 경로에서는
        // _ppvBits 가 이전 성공 할당의 유효 포인터다. 이론적으로 실패 한 번도 없이 첫 PaintDib
        // 이 호출되면 _ppvBits == Zero 지만 _currentWidth/Height 역시 0 이라 위 가드로 선차단.
        // 방어적 잔여 가드 — 예측 못한 상태 전이에서도 Span((void*)0, N) 으로 터지지 않도록.
        if (_ppvBits == IntPtr.Zero) return false;

        // 픽셀 버퍼 0 클리어
        unsafe
        {
            new Span<byte>((void*)_ppvBits, w * h * 4).Clear();
        }

        // 폰트 선택 → 콜백 → 폰트 복원.
        // 콜백은 외부(파사드)에서 주입되며 예외 전파 경로가 완전히 검증되지 않으므로
        // try/finally 로 SelectObject 복원을 보장한다. 복원을 놓치면 _memDC 에 SafeFontHandle
        // 의 HFONT 가 영구 선택된 상태가 되어, 다음 EnsureFont 에서 _currentFont.Dispose()
        // 가 GDI 리소스를 삭제해도 DC 안의 selected object 가 stale GDI 핸들로 남는다.
        IntPtr oldFont = IntPtr.Zero;
        if (_currentFont is not null)
            oldFont = Gdi32.SelectObject(_memDC, _currentFont.DangerousGetHandle());

        try
        {
            OverlayMetrics metrics = BuildMetrics(style, w, h);
            _renderToDib(_memDC, style, metrics);
        }
        finally
        {
            if (oldFont != IntPtr.Zero)
                Gdi32.SelectObject(_memDC, oldFont);
        }

        // 콜백 예외 시 아래는 실행되지 않아 _lastRenderedStyle 캐시가 갱신되지 않는다.
        // → 다음 Render 호출이 flip-flop 가드를 타지 않고 재시도 가능.
        ApplyPremultipliedAlpha(w, h);

        _lastRenderedStyle = style;
        return true;
    }

    /// <summary>페이드 프레임: SourceConstantAlpha만 변경.</summary>
    public void UpdateAlpha(byte alpha)
    {
        UpdateOverlay(_lastX, _lastY, _currentWidth, _currentHeight, alpha);
    }

    /// <summary>슬라이드 프레임: 위치만 변경 (비트맵 불변).</summary>
    public void UpdatePosition(int x, int y)
    {
        _lastX = x;
        _lastY = y;
        UpdateOverlay(x, y, _currentWidth, _currentHeight, _lastAlpha);
    }

    /// <summary>강조 프레임: 중심 기준 확대.</summary>
    public void UpdateScaledSize(int x, int y, int w, int h, byte alpha)
    {
        UpdateOverlay(x, y, w, h, alpha);
    }

    /// <summary>
    /// DPI/설정 변경 시 모든 캐시를 무효화한다. 다음 <see cref="Render"/> 호출이 리소스를
    /// 재생성하며, 이 메서드 자체는 블리트를 수행하지 않는다.
    /// </summary>
    public void HandleDpiChanged()
    {
        _cachedFontFamily = "";
        _cachedFontDpiScale = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        _fixedLabelWidth = 0;
        _cachedLabelDpiScale = 0; // 라벨 측정 캐시도 함께 무효화
        _lastRenderedStyle = null;
    }

    /// <summary>SetWindowPos HWND_TOPMOST 재적용.</summary>
    public void ForceTopmost()
    {
        if (_hwndOverlay != IntPtr.Zero)
            User32.SetWindowPos(_hwndOverlay, Win32Constants.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32Constants.SWP_NOMOVE | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOACTIVATE);
    }

    // ================================================================
    // 드래그
    // ================================================================

    /// <summary>
    /// 드래그 시작 (WM_ENTERSIZEMOVE). UpdateOverlay 억제 + Shift 축 잠금용 시작 좌표 캐치
    /// + 커서 hot point 캐치 + snapToWindows 유효 시 EnumWindows로 스냅 후보 rect 리스트 캐싱.
    /// </summary>
    public void BeginDrag(bool snapToWindows)
    {
        _isDragging = true;
        if (_hwndOverlay != IntPtr.Zero
            && User32.GetWindowRect(_hwndOverlay, out RECT rc))
        {
            _dragStartX = rc.Left;
            _dragStartY = rc.Top;

            if (User32.GetCursorPos(out POINT cursor))
            {
                _dragHotPointX = cursor.X - rc.Left;
                _dragHotPointY = cursor.Y - rc.Top;
            }
        }

        s_activeSnapRects.Clear();
        s_activeOwnerHwnd = _hwndOverlay;
        if (snapToWindows)
        {
            unsafe
            {
                User32.EnumWindows(&EnumWindowsCallback, IntPtr.Zero);
            }
        }
    }

    /// <summary>드래그 종료 (WM_EXITSIZEMOVE). 새 위치를 _lastX/_lastY에 반영 + 스냅 캐시 해제.</summary>
    public (int x, int y) EndDrag()
    {
        _isDragging = false;
        s_activeSnapRects.Clear();
        s_activeOwnerHwnd = IntPtr.Zero;
        if (_hwndOverlay != IntPtr.Zero
            && User32.GetWindowRect(_hwndOverlay, out RECT rc))
        {
            _lastX = rc.Left;
            _lastY = rc.Top;
        }
        return (_lastX, _lastY);
    }

    /// <summary>
    /// WM_MOVING 핸들러. 순서: 커서 기반 rect 재동기화 → Shift 축 잠금 → 창 엣지 스냅 → DPI 재계산.
    /// <para>
    /// 커서 재동기화가 필수인 이유: WM_MOVING의 movingRect은 이전 틱에 우리가 수정한 값을
    /// 새 base로 삼아 (base + cursor_delta)로 계산된다. 스냅으로 rect를 수정하면 그 델타가
    /// 시스템 base에 누적되어, 슬로우 드래그 시 창이 스냅 위치에 영구히 잠기고 커서는
    /// 계속 멀어진다. 매 틱 (cursor - hot_point)로 리셋하면 시스템의 누적 효과를 우회할 수 있다.
    /// </para>
    /// <para>
    /// rect를 항상 덮어쓰므로 리턴은 항상 true. w/h는 보존. Shift 잠금이 걸린 축은
    /// 스냅에서 제외되어 시작 좌표가 유지된다.
    /// </para>
    /// <paramref name="style"/>는 드래그 중 DPI 전환 시 리렌더용 — 파사드가 매 틱 <c>BuildStyle</c>로
    /// 합성해 넘겨야 한다.
    /// </summary>
    public bool HandleMoving(ref RECT movingRect, OverlayStyle style, bool snapToWindows, int snapThresholdPx, int snapGapPx)
    {
        if (!_isDragging) return false;

        int w = movingRect.Right - movingRect.Left;
        int h = movingRect.Bottom - movingRect.Top;

        if (User32.GetCursorPos(out POINT cursor))
        {
            movingRect.Left = cursor.X - _dragHotPointX;
            movingRect.Top = cursor.Y - _dragHotPointY;
            movingRect.Right = movingRect.Left + w;
            movingRect.Bottom = movingRect.Top + h;
        }

        bool xLocked = false;
        bool yLocked = false;

        if ((User32.GetAsyncKeyState(Win32Constants.VK_SHIFT) & 0x8000) != 0)
        {
            int dx = movingRect.Left - _dragStartX;
            int dy = movingRect.Top - _dragStartY;

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                // 가로 축 우세 → Y 잠금
                movingRect.Top = _dragStartY;
                movingRect.Bottom = _dragStartY + h;
                yLocked = true;
            }
            else
            {
                // 세로 축 우세 → X 잠금
                movingRect.Left = _dragStartX;
                movingRect.Right = _dragStartX + w;
                xLocked = true;
            }
        }

        if (snapToWindows)
            ApplySnap(ref movingRect, xLocked, yLocked, snapThresholdPx, snapGapPx);

        HandleDragDpiChange(movingRect.Left, movingRect.Top, style);

        return true;
    }

    /// <summary>
    /// _snapRects와 현재 위치의 모니터 work area를 후보로 하여 인디케이터 엣지를
    /// 가장 가까운 타겟 엣지에 스냅한다. X/Y 축 독립 처리, 잠긴 축은 건너뜀.
    /// 타겟 rect와의 수직/수평 겹침을 요구해 "멀리 떨어진 창"에 끌려가는 현상을 방지.
    /// work area는 인디가 항상 그 안에 있어 겹침 체크가 항상 통과 → 화면 엣지 스냅 성립.
    /// <paramref name="snapThresholdPx"/>는 pre-DPI 값이며 내부에서 DpiHelper.Scale로 변환.
    /// </summary>
    private bool ApplySnap(ref RECT movingRect, bool xLocked, bool yLocked, int snapThresholdPx, int snapGapPx)
    {
        int threshold = DpiHelper.Scale(snapThresholdPx, _currentDpiScale);
        int gap = DpiHelper.Scale(snapGapPx, _currentDpiScale);
        int w = movingRect.Right - movingRect.Left;
        int h = movingRect.Bottom - movingRect.Top;

        int bestDx = 0, bestDy = 0;
        int bestDistX = threshold + 1;
        int bestDistY = threshold + 1;

        // 현재 위치의 모니터 work area 추가 (화면 엣지 스냅 — 간격 없음)
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(
            movingRect.Left + w / 2, movingRect.Top + h / 2);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);
        ConsiderTarget(ref movingRect, workArea, xLocked, yLocked,
            ref bestDx, ref bestDy, ref bestDistX, ref bestDistY, gap: 0);

        // 창 엣지 스냅 — 경계선 겹침 방지 간격 적용
        foreach (RECT target in s_activeSnapRects)
        {
            ConsiderTarget(ref movingRect, target, xLocked, yLocked,
                ref bestDx, ref bestDy, ref bestDistX, ref bestDistY, gap);
        }

        bool modified = false;
        if (bestDistX <= threshold)
        {
            movingRect.Left += bestDx;
            movingRect.Right += bestDx;
            modified = true;
        }
        if (bestDistY <= threshold)
        {
            movingRect.Top += bestDy;
            movingRect.Bottom += bestDy;
            modified = true;
        }
        return modified;
    }

    /// <summary>
    /// 단일 타겟 rect에 대해 4개 엣지 쌍(L↔L, L↔R, R↔L, R↔R)과
    /// (T↔T, T↔B, B↔T, B↔B)을 검사해 최단 거리 스냅 후보를 갱신.
    /// Y 겹침이 있는 경우에만 X 엣지 스냅, X 겹침이 있는 경우에만 Y 엣지 스냅.
    /// </summary>
    private static void ConsiderTarget(
        ref RECT movingRect, RECT target,
        bool xLocked, bool yLocked,
        ref int bestDx, ref int bestDy,
        ref int bestDistX, ref int bestDistY,
        int gap)
    {
        bool yOverlap = movingRect.Top < target.Bottom && movingRect.Bottom > target.Top;
        if (!xLocked && yOverlap)
        {
            // gap 부호: inside(같은 쪽 엣지)는 안쪽으로, outside(반대 쪽)는 바깥으로
            TryEdge(target.Left + gap - movingRect.Left, ref bestDistX, ref bestDx);    // L-L inside
            TryEdge(target.Left - gap - movingRect.Right, ref bestDistX, ref bestDx);   // L-R outside
            TryEdge(target.Right + gap - movingRect.Left, ref bestDistX, ref bestDx);   // R-L outside
            TryEdge(target.Right - gap - movingRect.Right, ref bestDistX, ref bestDx);  // R-R inside
        }

        bool xOverlap = movingRect.Left < target.Right && movingRect.Right > target.Left;
        if (!yLocked && xOverlap)
        {
            TryEdge(target.Top + gap - movingRect.Top, ref bestDistY, ref bestDy);      // T-T inside
            TryEdge(target.Top - gap - movingRect.Bottom, ref bestDistY, ref bestDy);   // T-B outside
            TryEdge(target.Bottom + gap - movingRect.Top, ref bestDistY, ref bestDy);   // B-T outside
            TryEdge(target.Bottom - gap - movingRect.Bottom, ref bestDistY, ref bestDy);// B-B inside
        }
    }

    private static void TryEdge(int delta, ref int bestDist, ref int bestDelta)
    {
        int abs = Math.Abs(delta);
        if (abs < bestDist)
        {
            bestDist = abs;
            bestDelta = delta;
        }
    }

    /// <summary>
    /// 드래그 중 모니터 변경 시 DPI 재계산 + 리렌더 (WM_MOVING).
    /// 시스템 이동 루프가 위치를 제어하므로 제안 위치 기준으로 모니터 판별.
    /// <c>_isDragging</c> 가드를 우회하여 UpdateLayeredWindow 직접 호출.
    /// </summary>
    private void HandleDragDpiChange(int x, int y, OverlayStyle style)
    {
        if (!_isDragging) return;

        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x, y);
        (uint dpiX, uint dpiY) = DpiHelper.GetRawDpi(hMonitor);
        double dpiScale = dpiX / (double)DpiHelper.BASE_DPI;

        if (Math.Abs(dpiScale - _currentDpiScale) < DpiScaleTolerance) return;

        // DPI 캐시 리셋 + 리소스 재생성
        _fixedLabelWidth = 0;
        _currentWidth = 0;
        _currentHeight = 0;
        _cachedFontDpiScale = 0;
        _currentDpiScale = dpiScale;
        _currentDpiY = dpiY;
        _lastRenderedStyle = null;

        EnsureResources(style);
        CalculateFixedLabelWidth(style);

        int w = _currentWidth;
        int h = _currentHeight;
        if (w == 0 || h == 0) return;

        // DIB 그리기 재수행
        unsafe
        {
            new Span<byte>((void*)_ppvBits, w * h * 4).Clear();
        }

        // PaintDib 과 동일 이유로 SelectObject 복원을 finally 로 보장 (콜백 예외 전파 경로 대비).
        IntPtr oldFont = IntPtr.Zero;
        if (_currentFont is not null)
            oldFont = Gdi32.SelectObject(_memDC, _currentFont.DangerousGetHandle());

        try
        {
            OverlayMetrics metrics = BuildMetrics(style, w, h);
            _renderToDib(_memDC, style, metrics);
        }
        finally
        {
            if (oldFont != IntPtr.Zero)
                Gdi32.SelectObject(_memDC, oldFont);
        }

        ApplyPremultipliedAlpha(w, h);
        _lastRenderedStyle = style;

        // _isDragging 가드를 우회하여 UpdateLayeredWindow 직접 호출
        var ptDst = new POINT(x, y);
        var size = new SIZE(w, h);
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

    /// <summary>
    /// EnumWindows 콜백. BeginDrag에서 스냅 후보 rect를 s_activeSnapRects에 수집.
    /// [UnmanagedCallersOnly] + 함수 포인터 방식 (NativeAOT 권장).
    /// BOOL 리턴 (1 = 계속 열거, 0 = 중단).
    /// </summary>
    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == s_activeOwnerHwnd) return 1;
        if (!User32.IsWindowVisible(hwnd)) return 1;
        if (User32.IsIconic(hwnd)) return 1;
        if (Dwmapi.IsCloaked(hwnd)) return 1;
        if (!Dwmapi.TryGetVisibleFrame(hwnd, out RECT frame)) return 1;

        int w = frame.Right - frame.Left;
        int h = frame.Bottom - frame.Top;
        // 최소 크기 필터 — [UnmanagedCallersOnly] 정적 콜백이라 인스턴스 필드 접근 불가.
        // 상수 80px 을 Core 레이어 private const 로 직접 보관한다.
        if (w < SnapMinWindowSizePx || h < SnapMinWindowSizePx)
            return 1;

        s_activeSnapRects.Add(frame);
        return 1;
    }

    /// <summary>
    /// 스냅 후보 최소 크기 (DPI 미적용 px). [UnmanagedCallersOnly] 정적 콜백에서
    /// 인스턴스 필드 접근 불가라 Core 레이어 private const 로 직접 보관.
    /// </summary>
    private const int SnapMinWindowSizePx = 80;


    // ================================================================
    // 리소스 관리
    // ================================================================

    /// <summary>DPI 변경 시 캐시 리셋 (모니터 이동 또는 초기화 후 첫 Show).</summary>
    private void UpdateDpiFromPoint(int x, int y)
    {
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(x, y);
        (uint dpiX, uint dpiY) = DpiHelper.GetRawDpi(hMonitor);
        double dpiScale = dpiX / (double)DpiHelper.BASE_DPI;

        if (Math.Abs(dpiScale - _currentDpiScale) > DpiScaleTolerance)
        {
            _fixedLabelWidth = 0;
            _currentWidth = 0;
            _currentHeight = 0;
            _cachedFontDpiScale = 0;
            _lastRenderedStyle = null;
        }

        _currentDpiScale = dpiScale;
        _currentDpiY = dpiY;
    }

    private void EnsureResources(OverlayStyle style)
    {
        // IndicatorScale은 OverlayStyle이 들어오기 전에 파사드가 이미 곱했으므로 여기서 재곱 불필요.
        _baseWidth = style.LabelWidthLogicalPx;
        _baseHeight = style.LabelHeightLogicalPx;

        int targetW = DpiHelper.Scale(_baseWidth, _currentDpiScale);
        int targetH = DpiHelper.Scale(_baseHeight, _currentDpiScale);

        EnsureFont(style);

        if (_fixedLabelWidth > 0)
            targetW = _fixedLabelWidth;

        EnsureDib(targetW, targetH);
    }

    private void EnsureFont(OverlayStyle style)
    {
        // 캐시 키는 scale이 곱해진 최종 폰트 크기(logical px, DPI 미적용)로 저장.
        // DPI 변경 시 _cachedFontDpiScale 비교로 캐시 미스 유도.
        int scaledFontSize = style.FontSizeLogicalPx;

        if (style.FontFamily == _cachedFontFamily
            && scaledFontSize == _cachedFontSize
            && style.IsBold == _cachedFontIsBold
            && Math.Abs(_currentDpiScale - _cachedFontDpiScale) < DpiScaleTolerance)
            return;

        // MulDiv로 정수 정밀도를 유지하며 DPI 곱셈 — 단순 round 대체 금지 (라벨 폭 1px 회귀 위험).
        int fontHeight = -Kernel32.MulDiv(scaledFontSize, (int)_currentDpiY, 72);
        IntPtr hFont = Gdi32.CreateFontW(
            fontHeight, 0, 0, 0,
            style.IsBold ? Win32Constants.FW_BOLD : Win32Constants.FW_NORMAL,
            0, 0, 0,
            Win32Constants.DEFAULT_CHARSET,
            Win32Constants.OUT_TT_PRECIS,
            Win32Constants.CLIP_DEFAULT_PRECIS,
            Win32Constants.CLEARTYPE_QUALITY,
            Win32Constants.DEFAULT_PITCH,
            style.FontFamily);

        // CreateFontW 실패 시 기존 폰트/캐시를 유지해 다음 EnsureFont 호출에서 재시도를 유도.
        // 실패 후에도 캐시를 갱신하면 같은 파라미터로는 캐시 히트되어 영원히 재진입 없이
        // 빈 HFONT로 DrawText 가 실패하는 상태에 고착된다. Dispose 도 실패 경로에서는 생략.
        if (hFont == IntPtr.Zero)
        {
            Logger.Warning($"CreateFontW failed: family='{style.FontFamily}' size={scaledFontSize} bold={style.IsBold}");
            return;
        }

        _currentFont?.Dispose();
        _currentFont = new SafeFontHandle(hFont, true);
        _cachedFontFamily = style.FontFamily;
        _cachedFontSize = scaledFontSize;
        _cachedFontIsBold = style.IsBold;
        _cachedFontDpiScale = _currentDpiScale;

        // 새 폰트의 메트릭을 측정해 vCenter 보정값 갱신.
        // 보정 = (tmInternalLeading - tmDescent) / 2  (>0이면 텍스트를 위로 그만큼 이동)
        // tmInternalLeading은 라틴 액센트용 상단 reserved 공간. 한글/대문자는 이 영역을 쓰지 않아
        // tmInternalLeading > tmDescent인 폰트(맑은 고딕 등)는 글리프가 cell 중앙보다 아래로 치우친다.
        // GDI는 폰트가 SelectObject된 상태에서만 메트릭을 반환하므로 _memDC에 임시 SelectObject.
        IntPtr oldFont = Gdi32.SelectObject(_memDC, hFont);
        if (Gdi32.GetTextMetricsW(_memDC, out TEXTMETRICW tm))
            _textVCenterOffsetPx = (tm.tmInternalLeading - tm.tmDescent) / 2;
        else
            _textVCenterOffsetPx = 0;
        if (oldFont != IntPtr.Zero)
            Gdi32.SelectObject(_memDC, oldFont);
    }

    private void EnsureDib(int width, int height)
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
            out IntPtr ppvBits, IntPtr.Zero, 0);

        if (hBitmap == IntPtr.Zero)
        {
            if (!_dibFailureLogged)
            {
                _dibFailureLogged = true;
                Logger.Warning($"CreateDIBSection failed: {width}x{height}. Keeping previous DIB.");
            }
            return;
        }

        _dibFailureLogged = false;
        _ppvBits = ppvBits;

        Gdi32.SelectObject(_memDC, hBitmap);
        _currentBitmap?.Dispose();
        _currentBitmap = new SafeBitmapHandle(hBitmap, true);

        _currentWidth = width;
        _currentHeight = height;
        _lastRenderedStyle = null;
    }

    /// <summary>
    /// 3종 라벨의 텍스트 폭을 측정해 <c>_fixedLabelWidth</c>를 고정한다. 상태 전환 시 라벨 폭이
    /// 변동하면 DIB 재생성이 일어나 깜빡임이 발생하므로 3종 중 최대 폭 + 패딩으로 고정.
    /// 고정 폭이 현재 DIB 폭과 다르면 DIB를 재생성한다.
    /// <para>
    /// 캐시 키(레이블 3종 + 폰트 서명 + DPI + 패딩 + 최소폭) 가 불변이면 GetTextExtentPoint32W
    /// 3회를 생략한다. 설정 리로드/DPI 전환/리사이즈 같은 경로에서만 재측정이 필요하다.
    /// </para>
    /// </summary>
    private void CalculateFixedLabelWidth(OverlayStyle style)
    {
        if (_currentFont is null) return;

        // 캐시 히트 조건: 키 전부 일치 + _fixedLabelWidth 이미 계산됨.
        // 폰트 서명은 EnsureFont 가 방금 갱신한 _cachedFont* 를 그대로 비교한다.
        if (_fixedLabelWidth > 0
            && _cachedLabelMeasureKey == style.MeasureLabels
            && _cachedLabelPaddingLogicalPx == style.PaddingXLogicalPx
            && _cachedLabelMinWidthLogicalPx == style.LabelWidthLogicalPx
            && Math.Abs(_cachedLabelDpiScale - _currentDpiScale) < DpiScaleTolerance
            && _cachedLabelFontFamily == _cachedFontFamily
            && _cachedLabelFontSize == _cachedFontSize
            && _cachedLabelFontIsBold == _cachedFontIsBold)
        {
            // EnsureDib 은 EnsureResources 에서 이미 _fixedLabelWidth 기준으로 호출되어
            // _currentWidth 가 일치하는 상태다 — 추가 DIB 재생성 불필요.
            return;
        }

        IntPtr oldFont = Gdi32.SelectObject(_memDC, _currentFont.DangerousGetHandle());

        int maxTextWidth = 0;
        (string hangul, string english, string nonKorean) = style.MeasureLabels;
        foreach (string label in new[] { hangul, english, nonKorean })
        {
            Gdi32.GetTextExtentPoint32W(_memDC, label, label.Length, out SIZE sz);
            if (sz.cx > maxTextWidth) maxTextWidth = sz.cx;
        }

        Gdi32.SelectObject(_memDC, oldFont);

        int padding = 2 * DpiHelper.Scale(style.PaddingXLogicalPx, _currentDpiScale);
        int calculated = maxTextWidth + padding;
        int minWidth = DpiHelper.Scale(style.LabelWidthLogicalPx, _currentDpiScale);
        _fixedLabelWidth = Math.Max(calculated, minWidth);

        _cachedLabelMeasureKey = style.MeasureLabels;
        _cachedLabelPaddingLogicalPx = style.PaddingXLogicalPx;
        _cachedLabelMinWidthLogicalPx = style.LabelWidthLogicalPx;
        _cachedLabelDpiScale = _currentDpiScale;
        _cachedLabelFontFamily = _cachedFontFamily;
        _cachedLabelFontSize = _cachedFontSize;
        _cachedLabelFontIsBold = _cachedFontIsBold;

        if (_fixedLabelWidth != _currentWidth)
        {
            int labelH = DpiHelper.Scale(style.LabelHeightLogicalPx, _currentDpiScale);
            EnsureDib(_fixedLabelWidth, labelH);
        }
    }

    /// <summary>현재 DPI 상태와 스타일에서 콜백에 전달할 OverlayMetrics 조립.</summary>
    private OverlayMetrics BuildMetrics(OverlayStyle style, int w, int h)
    {
        int scaledPadding = DpiHelper.Scale(style.PaddingXLogicalPx, _currentDpiScale);
        int scaledBorderW = DpiHelper.Scale(style.BorderWidthLogicalPx, _currentDpiScale);
        int scaledBorderR = DpiHelper.Scale(style.BorderRadiusLogicalPx, _currentDpiScale);
        int fontHeightPx = -Kernel32.MulDiv(style.FontSizeLogicalPx, (int)_currentDpiY, 72);

        return new OverlayMetrics(
            DpiScale: _currentDpiScale,
            DpiY: _currentDpiY,
            ScaledWidth: w,
            ScaledHeight: h,
            ScaledPaddingX: scaledPadding,
            ScaledBorderWidth: scaledBorderW,
            ScaledBorderRadius: scaledBorderR,
            ScaledFontHeightPx: fontHeightPx,
            TextVCenterOffsetPx: _textVCenterOffsetPx);
    }

    // ================================================================
    // Premultiplied alpha 후처리
    // ================================================================

    /// <summary>
    /// GDI 출력은 non-premultiplied 상태이므로 UpdateLayeredWindow(ULW_ALPHA)에 전달하기 전에
    /// RGB 채널에 alpha를 사전 곱셈한다. alpha가 0인데 RGB가 0이 아닌 픽셀은 DrawTextW의
    /// 안티앨리어싱 엣지로 생기므로 alpha를 255로 올려 보존.
    /// </summary>
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

    // ================================================================
    // UpdateLayeredWindow 래퍼
    // ================================================================

    private void UpdateOverlay(int x, int y, int displayW, int displayH, byte alpha)
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
}
