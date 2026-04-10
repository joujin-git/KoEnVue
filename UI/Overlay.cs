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
    private static int _dragStartX;
    private static int _dragStartY;
    // 드래그 시작 시 커서가 창 안에서 잡고 있는 상대 오프셋(hot point).
    // HandleMoving에서 매 틱 (커서 - hot point)로 rect를 재동기화하기 위한 기준.
    // WM_MOVING이 우리 수정값을 새 base로 삼아 다음 틱 proposed를 계산하는 탓에
    // 스냅 델타가 누적되어 창과 커서가 벌어지는 현상을 방지한다.
    private static int _dragHotPointX;
    private static int _dragHotPointY;

    // 드래그 스냅 후보 — BeginDrag에서 EnumWindows로 한 번만 캐싱,
    // 드래그 중 매 WM_MOVING 틱에서 순회. EndDrag에서 Clear.
    private static readonly List<RECT> _snapRects = new(64);

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

    /// <summary>
    /// 드래그 시작 (WM_ENTERSIZEMOVE). UpdateOverlay 억제 + Shift 축 잠금용 시작 좌표 캐치
    /// + 커서 hot point 캐치 + SnapToWindows 유효 시 EnumWindows로 스냅 후보 rect 리스트 캐싱.
    /// </summary>
    public static void BeginDrag(AppConfig config)
    {
        _isDragging = true;
        if (_hwndOverlay != IntPtr.Zero)
        {
            User32.GetWindowRect(_hwndOverlay, out RECT rc);
            _dragStartX = rc.Left;
            _dragStartY = rc.Top;

            if (User32.GetCursorPos(out POINT cursor))
            {
                _dragHotPointX = cursor.X - rc.Left;
                _dragHotPointY = cursor.Y - rc.Top;
            }
        }

        _snapRects.Clear();
        if (config.SnapToWindows)
        {
            unsafe
            {
                User32.EnumWindows(&EnumWindowsCallback, IntPtr.Zero);
            }
        }
    }

    /// <summary>
    /// EnumWindows 콜백. BeginDrag에서 스냅 후보 rect를 _snapRects에 수집.
    /// [UnmanagedCallersOnly] + 함수 포인터 방식 (NativeAOT 권장).
    /// BOOL 리턴 (1 = 계속 열거, 0 = 중단).
    /// </summary>
    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == _hwndOverlay) return 1;
        if (!User32.IsWindowVisible(hwnd)) return 1;
        if (User32.IsIconic(hwnd)) return 1;
        if (Dwmapi.IsCloaked(hwnd)) return 1;
        if (!Dwmapi.TryGetVisibleFrame(hwnd, out RECT frame)) return 1;

        int w = frame.Right - frame.Left;
        int h = frame.Bottom - frame.Top;
        if (w < DefaultConfig.SnapMinWindowSizePx || h < DefaultConfig.SnapMinWindowSizePx)
            return 1;

        _snapRects.Add(frame);
        return 1;
    }

    /// <summary>
    /// WM_MOVING 핸들러. 순서: 커서 기반 rect 재동기화 → Shift 축 잠금 → 창 엣지 스냅 → DPI 재계산.
    ///
    /// 커서 재동기화가 필수인 이유: WM_MOVING의 movingRect은 이전 틱에 우리가 수정한 값을
    /// 새 base로 삼아 (base + cursor_delta)로 계산된다. 스냅으로 rect를 수정하면 그 델타가
    /// 시스템 base에 누적되어, 슬로우 드래그 시 창이 스냅 위치에 영구히 잠기고 커서는
    /// 계속 멀어진다. 매 틱 (cursor - hot_point)로 리셋하면 시스템의 누적 효과를 우회할 수 있다.
    ///
    /// rect를 항상 덮어쓰므로 리턴은 항상 true. w/h는 보존.
    /// Shift 잠금이 걸린 축은 스냅에서 제외되어 시작 좌표가 유지된다.
    /// </summary>
    public static bool HandleMoving(ref RECT movingRect, ImeState state, AppConfig config)
    {
        if (!_isDragging) return false;

        int w = movingRect.Right - movingRect.Left;
        int h = movingRect.Bottom - movingRect.Top;

        if (User32.GetCursorPos(out POINT cursor))
        {
            movingRect.Left   = cursor.X - _dragHotPointX;
            movingRect.Top    = cursor.Y - _dragHotPointY;
            movingRect.Right  = movingRect.Left + w;
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

        if (config.SnapToWindows)
            ApplySnap(ref movingRect, xLocked, yLocked);

        HandleDragDpiChange(movingRect.Left, movingRect.Top, state, config);

        return true;
    }

    /// <summary>
    /// _snapRects와 현재 위치의 모니터 work area를 후보로 하여 인디케이터 엣지를
    /// 가장 가까운 타겟 엣지에 스냅한다. X/Y 축 독립 처리, 잠긴 축은 건너뜀.
    /// 타겟 rect와의 수직/수평 겹침을 요구해 "멀리 떨어진 창"에 끌려가는 현상을 방지.
    /// work area는 인디가 항상 그 안에 있어 겹침 체크가 항상 통과 → 화면 엣지 스냅 성립.
    /// </summary>
    private static bool ApplySnap(ref RECT movingRect, bool xLocked, bool yLocked)
    {
        int threshold = DpiHelper.Scale(DefaultConfig.SnapThresholdPx, _currentDpiScale);
        int w = movingRect.Right - movingRect.Left;
        int h = movingRect.Bottom - movingRect.Top;

        int bestDx = 0, bestDy = 0;
        int bestDistX = threshold + 1;
        int bestDistY = threshold + 1;

        // 현재 위치의 모니터 work area 추가 (화면 엣지 스냅)
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(
            movingRect.Left + w / 2, movingRect.Top + h / 2);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);
        ConsiderTarget(ref movingRect, workArea, xLocked, yLocked,
            ref bestDx, ref bestDy, ref bestDistX, ref bestDistY);

        foreach (RECT target in _snapRects)
        {
            ConsiderTarget(ref movingRect, target, xLocked, yLocked,
                ref bestDx, ref bestDy, ref bestDistX, ref bestDistY);
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
        ref int bestDistX, ref int bestDistY)
    {
        bool yOverlap = movingRect.Top < target.Bottom && movingRect.Bottom > target.Top;
        if (!xLocked && yOverlap)
        {
            TryEdge(target.Left  - movingRect.Left,  ref bestDistX, ref bestDx);
            TryEdge(target.Left  - movingRect.Right, ref bestDistX, ref bestDx);
            TryEdge(target.Right - movingRect.Left,  ref bestDistX, ref bestDx);
            TryEdge(target.Right - movingRect.Right, ref bestDistX, ref bestDx);
        }

        bool xOverlap = movingRect.Left < target.Right && movingRect.Right > target.Left;
        if (!yLocked && xOverlap)
        {
            TryEdge(target.Top    - movingRect.Top,    ref bestDistY, ref bestDy);
            TryEdge(target.Top    - movingRect.Bottom, ref bestDistY, ref bestDy);
            TryEdge(target.Bottom - movingRect.Top,    ref bestDistY, ref bestDy);
            TryEdge(target.Bottom - movingRect.Bottom, ref bestDistY, ref bestDy);
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
    /// </summary>
    private static void HandleDragDpiChange(int x, int y, ImeState state, AppConfig config)
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

    /// <summary>드래그 종료 (WM_EXITSIZEMOVE). 새 위치를 _lastX/_lastY에 반영 + 스냅 캐시 해제.</summary>
    public static (int x, int y) EndDrag()
    {
        _isDragging = false;
        _snapRects.Clear();
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
    /// - 시스템 입력 프로세스(시작 메뉴, 검색 창): 포그라운드 창의 시각적 왼쪽 위 모서리 바로 위쪽.
    ///   창 rect는 DWM extended frame bounds로 받아 invisible resize border를 배제한다.
    ///   config.DefaultIndicatorPosition은 이 분기에 적용되지 않음.
    /// - 일반 프로세스: config.DefaultIndicatorPosition이 있으면 해당 모서리 anchor + delta로 계산,
    ///   없으면 DefaultConfig.DefaultIndicatorOffset* 폴백(work area 우상단 근처).
    /// </summary>
    public static (int x, int y) GetDefaultPosition(IntPtr hwndForeground, string processName, AppConfig config)
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

        if (config.DefaultIndicatorPosition is { } anchor)
            return ResolveAnchor(workArea, anchor);

        return (workArea.Right + DefaultConfig.DefaultIndicatorOffsetX,
                workArea.Top   + DefaultConfig.DefaultIndicatorOffsetY);
    }

    /// <summary>
    /// Corner anchor + delta를 work area 기준 절대 좌표로 변환.
    /// </summary>
    private static (int x, int y) ResolveAnchor(RECT workArea, DefaultPositionConfig anchor)
    {
        int x = anchor.Corner is Corner.TopLeft or Corner.BottomLeft
            ? workArea.Left + anchor.DeltaX
            : workArea.Right + anchor.DeltaX;
        int y = anchor.Corner is Corner.TopLeft or Corner.TopRight
            ? workArea.Top + anchor.DeltaY
            : workArea.Bottom + anchor.DeltaY;
        return (x, y);
    }

    /// <summary>
    /// 현재 인디 위치(`_lastX, _lastY`)에서 가장 가까운 모서리를 찾아
    /// DefaultPositionConfig로 환산. 트레이 "기본 위치 → 현재 위치로 설정"에서 호출.
    /// 모니터는 현재 위치 기준으로 판정하므로 사용자가 멀티모니터 중 어느 화면에
    /// 인디를 뒀든 해당 화면의 work area가 anchor 기준이 된다.
    /// 인디가 한 번도 표시된 적이 없어 좌표가 (0,0)이면 null.
    /// </summary>
    public static DefaultPositionConfig? ComputeAnchorFromCurrentPosition()
    {
        if (_lastX == 0 && _lastY == 0)
            return null;

        POINT pt = new(_lastX, _lastY);
        IntPtr hMonitor = User32.MonitorFromPoint(pt, Win32Constants.MONITOR_DEFAULTTONEAREST);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);

        // 4개 모서리까지의 맨해튼 거리를 각각 계산하여 최소값 선택.
        (Corner corner, int dx, int dy) best = (Corner.TopRight, 0, 0);
        long bestDist = long.MaxValue;

        void Consider(Corner c, int ax, int ay)
        {
            int dx = _lastX - ax;
            int dy = _lastY - ay;
            long dist = Math.Abs((long)dx) + Math.Abs((long)dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = (c, dx, dy);
            }
        }

        Consider(Corner.TopLeft,     workArea.Left,  workArea.Top);
        Consider(Corner.TopRight,    workArea.Right, workArea.Top);
        Consider(Corner.BottomLeft,  workArea.Left,  workArea.Bottom);
        Consider(Corner.BottomRight, workArea.Right, workArea.Bottom);

        return new DefaultPositionConfig
        {
            Corner = best.corner,
            DeltaX = best.dx,
            DeltaY = best.dy,
        };
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
        // IndicatorScale은 DPI 스케일링 전 base 픽셀에 곱해지는 배율 (1.0~5.0, 0.1 단위).
        double scale = config.IndicatorScale;
        _baseWidth = (int)Math.Round(config.LabelWidth * scale);
        _baseHeight = (int)Math.Round(config.LabelHeight * scale);

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
        // 캐시 키는 scale이 곱해진 최종 폰트 크기로 저장 — scale 변경 시 캐시 미스 유도.
        int scaledFontSize = (int)Math.Round(config.FontSize * config.IndicatorScale);

        if (config.FontFamily == _cachedFontFamily
            && scaledFontSize == _cachedFontSize
            && config.FontWeight == _cachedFontWeight
            && Math.Abs(_currentDpiScale - _cachedFontDpiScale) < 0.001)
            return;

        _currentFont?.Dispose();

        int fontHeight = -Kernel32.MulDiv(scaledFontSize, (int)_currentDpiY, 72);
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
        _cachedFontSize = scaledFontSize;
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

        double scale = config.IndicatorScale;
        int padding = 2 * DpiHelper.Scale((int)Math.Round(DefaultConfig.LABEL_PADDING_X * scale), _currentDpiScale);
        int calculated = maxTextWidth + padding;
        int minWidth = DpiHelper.Scale((int)Math.Round(config.LabelWidth * scale), _currentDpiScale);
        _fixedLabelWidth = Math.Max(calculated, minWidth);

        if (_fixedLabelWidth != _currentWidth)
        {
            int labelH = DpiHelper.Scale((int)Math.Round(config.LabelHeight * scale), _currentDpiScale);
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
        double scale = config.IndicatorScale;
        int radius = DpiHelper.Scale((int)Math.Round(config.LabelBorderRadius * scale), _currentDpiScale);
        Gdi32.RoundRect(_memDC, 0, 0, w, h, radius, radius);
        Gdi32.SelectObject(_memDC, oldBrush);

        // 5. 테두리 (border_width > 0)
        int borderW = DpiHelper.Scale((int)Math.Round(config.BorderWidth * scale), _currentDpiScale);
        if (borderW > 0)
        {
            uint borderColor = ColorHelper.HexToColorRef(config.BorderColor);
            IntPtr hBorderPen = Gdi32.CreatePen(Win32Constants.PS_SOLID, borderW, borderColor);
            IntPtr hNullBrush = Gdi32.GetStockObject(Win32Constants.NULL_BRUSH);

            IntPtr oldBorderPen = Gdi32.SelectObject(_memDC, hBorderPen);
            IntPtr oldBorderBrush = Gdi32.SelectObject(_memDC, hNullBrush);

            int halfBorder = borderW / 2;
            int borderRadius = DpiHelper.Scale((int)Math.Round(config.LabelBorderRadius * scale), _currentDpiScale);
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
