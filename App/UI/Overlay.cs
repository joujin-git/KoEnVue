using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI;

/// <summary>
/// 정적 파사드 — LayeredOverlayBase 엔진에 AppConfig + ImeState를 합성해 위임.
/// 모든 호출은 메인 스레드에서만 수행. Program.cs / Animation.cs / Tray.cs의 기존 호출
/// 표현식(<c>Overlay.X(...)</c>)은 변경되지 않는다.
/// <para>
/// 파사드에 남는 상태는 <c>_config</c>(AppConfig 캐시) + <c>_engine</c>(LayeredOverlayBase
/// 인스턴스) 단 2개 필드. 리소스 생명주기·드래그·DPI 처리·premultiplied alpha·블리트는 전부
/// 엔진이 소유하며, 파사드는 ImeState + AppConfig → OverlayStyle 변환과 실제 GDI 그리기
/// 콜백(<see cref="OnRenderToDib"/>)만 담당한다.
/// </para>
/// </summary>
internal static class Overlay
{
    // ================================================================
    // 상태 (Stage 4-A: 2개 필드만 유지)
    // ================================================================

    private static LayeredOverlayBase _engine = null!;

    // Stage 3-A: 현재 AppConfig 캐시. Initialize/HandleConfigChanged 시점에 주입되며
    // public 메서드 내부에서 BuildStyle을 통해 OverlayStyle로 합성된다.
    private static AppConfig _config = null!;

    // CAPS LOCK 토글 상태. Program.cs의 메인 스레드 WM_TIMER 폴러가 SetCapsLock으로 주입한다.
    // BuildStyle이 매 렌더마다 읽어 OverlayStyle.CapsLockOn 필드에 반영 — record 값 동등성
    // 비교를 통해 상태 전환 시 DIB 재렌더가 자동으로 일어난다.
    private static bool _capsLockOn;

    // ================================================================
    // 초기화 / 해제
    // ================================================================

    public static void Initialize(IntPtr hwndOverlay, AppConfig config)
    {
        _config = config;
        // 사용자가 CAPS LOCK을 이미 켠 상태로 앱을 시작해도 첫 렌더가 정확하도록 초기값 주입.
        // Program.cs의 WM_TIMER 폴러도 같은 초기값을 읽어 첫 틱에서 중복 WM_TIMER 콜백이
        // Overlay.UpdateColor를 발생시키지 않는다.
        _capsLockOn = (User32.GetKeyState(Win32Constants.VK_CAPITAL) & 1) != 0;
        _engine = new LayeredOverlayBase(hwndOverlay, OnRenderToDib);
        // DIB/HFONT 사전 생성 — 첫 Show 이전에 호출되는 GetDefaultPosition 경로가
        // _currentHeight > 0을 가정하므로 초기화 시점에 리소스를 warming한다. 그리기는 생략.
        _engine.PrepareResources(BuildStyle(config, ImeState.Hangul));
    }

    public static void Dispose()
    {
        _engine?.Dispose();
    }

    // ================================================================
    // Public API (Program.cs / Animation.cs / Tray.cs에서 호출)
    // ================================================================

    /// <summary>
    /// 지정 좌표에 인디케이터 렌더 + UpdateLayeredWindow.
    /// 좌표가 속한 모니터 DPI로 리소스 갱신.
    /// </summary>
    public static void Show(int x, int y, ImeState state)
    {
        _engine.Show(x, y);
        _engine.Render(BuildStyle(_config, state));
    }

    /// <summary>즉시 색상 변경 (비트맵 갱신 + premultiply).</summary>
    public static void UpdateColor(ImeState state)
    {
        _engine.Render(BuildStyle(_config, state));
    }

    /// <summary>페이드 프레임: SourceConstantAlpha만 변경.</summary>
    public static void UpdateAlpha(byte alpha)
    {
        _engine.UpdateAlpha(alpha);
    }

    /// <summary>슬라이드 프레임: 위치만 변경 (비트맵 불변).</summary>
    public static void UpdatePosition(int x, int y)
    {
        _engine.UpdatePosition(x, y);
    }

    /// <summary>강조 프레임: 중심 기준 확대.</summary>
    public static void UpdateScaledSize(int x, int y, int w, int h, byte alpha)
    {
        _engine.UpdateScaledSize(x, y, w, h, alpha);
    }

    /// <summary>ShowWindow(SW_HIDE) + 상태 리셋.</summary>
    public static void Hide()
    {
        _engine.Hide();
    }

    /// <summary>DPI 변경 시 HFONT/DIB 재생성.</summary>
    public static void HandleDpiChanged()
    {
        _engine.HandleDpiChanged();
        // 즉시 재생성 — 이후 UpdateAlpha/Show가 유효한 _currentWidth/Height를 기대.
        // 상태 필드는 DIB 치수에 영향이 없으므로 임의 상태 OK.
        _engine.PrepareResources(BuildStyle(_config, ImeState.Hangul));
    }

    /// <summary>설정 변경 시 전체 리소스 리빌드.</summary>
    public static void HandleConfigChanged(AppConfig config)
    {
        _config = config;
        _engine.HandleDpiChanged();  // 캐시 무효화 (DPI뿐 아니라 폰트/라벨 모두)
        _engine.PrepareResources(BuildStyle(config, ImeState.Hangul));
    }

    /// <summary>
    /// 드래그 시작 (WM_ENTERSIZEMOVE). Stage 3-A: config 레코드 대신 primitive bool.
    /// </summary>
    public static void BeginDrag(bool snapToWindows)
    {
        _engine.BeginDrag(snapToWindows);
    }

    /// <summary>
    /// WM_MOVING 핸들러. Stage 3-A: config 레코드 대신 primitive 두 개.
    /// 드래그 중 DPI 전환 시 리렌더용 OverlayStyle을 매 틱 합성해 엔진에 전달.
    /// </summary>
    public static bool HandleMoving(ref RECT movingRect, ImeState state, bool snapToWindows, int snapThresholdPx, int snapGapPx)
    {
        return _engine.HandleMoving(ref movingRect, BuildStyle(_config, state), snapToWindows, snapThresholdPx, snapGapPx);
    }

    /// <summary>드래그 종료 (WM_EXITSIZEMOVE).</summary>
    public static (int x, int y) EndDrag()
    {
        return _engine.EndDrag();
    }

    /// <summary>SetWindowPos HWND_TOPMOST 재적용.</summary>
    public static void ForceTopmost()
    {
        _engine.ForceTopmost();
    }

    /// <summary>강조 스케일 계산용.</summary>
    public static (int w, int h) GetBaseSize() => _engine.GetBaseSize();

    /// <summary>강조 중심점 계산용.</summary>
    public static (int x, int y) GetLastPosition() => _engine.GetLastPosition();

    /// <summary>현재 가시성.</summary>
    public static bool IsVisible => _engine.IsVisible;

    /// <summary>
    /// CAPS LOCK 토글 상태 주입. Program.cs의 메인 스레드 WM_TIMER 폴러가 호출.
    /// 실제 재렌더는 호출자가 이후 <see cref="UpdateColor"/>로 트리거한다 — 이 메서드는
    /// 필드 갱신만 담당하므로 인디가 숨겨진 상태에서도 다음 표시 시점에 정확히 반영된다.
    /// </summary>
    public static void SetCapsLock(bool capsLockOn)
    {
        _capsLockOn = capsLockOn;
    }

    // ================================================================
    // GetDefaultPosition + ComputeAnchorFromCurrentPosition (system input 특수 로직 포함)
    // ================================================================

    /// <summary>시스템 입력 프로세스 기본 위치의 창 상단 여백(px).</summary>
    private const int SystemInputGapPx = 4;

    /// <summary>
    /// 직전 시스템 입력 프로세스에서 관찰된 유효(비전체화면) DWM 프레임.
    /// StartMenuExperienceHost처럼 CoreWindow가 화면 전체를 덮는 경우,
    /// 직전 SearchHost 등이 남긴 실제 패널 프레임을 재사용해 위치를 보정한다.
    /// 메인 스레드에서만 접근하므로 동기화 불필요.
    /// </summary>
    private static RECT _lastValidSystemInputFrame;

    /// <summary>CAPS LOCK 막대 두께 (logical px, DPI 스케일링 전).</summary>
    private const int CapsLockBarWidthLogicalPx = 2;

    /// <summary>
    /// CAPS LOCK 막대 수평 인셋 최소값 (logical px). 보더가 있으면 borderWidth와 max.
    /// 1 logical px은 시각적으로 너무 작아 대칭이 흐려지므로 2로 설정.
    /// </summary>
    private const int CapsLockBarInsetLogicalPx = 2;

    /// <summary>
    /// CAPS LOCK 막대 우측 시각 보정 (physical px).
    /// 수학적으론 대칭이지만 RoundRect의 우/하단 exclusive 규칙 + DrawTextW AA 가중치 +
    /// premultiplied alpha 합성이 겹치며 시각적으로 우측 gap이 1px 좁아 보이는 현상을 보정.
    /// </summary>
    private const int CapsLockRightCompensationPx = 1;

    /// <summary>
    /// 앱별 저장 위치가 없을 때 기본 위치.
    /// - 시스템 입력 프로세스(시작 메뉴, 검색 창): 포그라운드 창의 시각적 왼쪽 위 모서리 바로 위쪽.
    ///   창 rect는 DWM extended frame bounds로 받아 invisible resize border를 배제한다.
    ///   config.DefaultIndicatorPosition은 이 분기에 적용되지 않음.
    /// - 일반 프로세스: config.DefaultIndicatorPosition이 있으면 해당 모서리 anchor + delta로 계산,
    ///   없으면 DefaultConfig.DefaultIndicatorOffset* 폴백(work area 우상단 근처).
    /// </summary>
    public static (int x, int y) GetDefaultPosition(IntPtr hwndForeground, string processName)
    {
        IntPtr hMonitor = (hwndForeground != IntPtr.Zero)
            ? User32.MonitorFromWindow(hwndForeground, Win32Constants.MONITOR_DEFAULTTOPRIMARY)
            : User32.MonitorFromWindow(_engine.Hwnd, Win32Constants.MONITOR_DEFAULTTOPRIMARY);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);

        if (DefaultConfig.IsSystemInputProcess(processName)
            && hwndForeground != IntPtr.Zero
            && Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT frame))
        {
            // CoreWindow 계열(StartMenuExperienceHost 등)은 DWM extended frame bounds가
            // 화면 전체를 덮어 시각적 패널 위치를 반영하지 않는다.
            // 직전 SearchHost 등이 남긴 유효 프레임이 있으면 재사용하고,
            // 없으면 일반 기본 위치로 폴스루한다.
            bool isFullScreen = frame.Left <= workArea.Left
                && frame.Top <= workArea.Top
                && frame.Right >= workArea.Right
                && frame.Bottom >= workArea.Bottom;

            if (!isFullScreen)
            {
                _lastValidSystemInputFrame = frame;
            }
            else if (_lastValidSystemInputFrame.Right > _lastValidSystemInputFrame.Left)
            {
                // 캐시된 유효 프레임으로 대체 (SearchHost → StartMenuExperienceHost 전환)
                frame = _lastValidSystemInputFrame;
                isFullScreen = false;
            }

            if (!isFullScreen)
            {
                (int _, int labelH) = _engine.GetBaseSize();
                if (labelH <= 0)
                {
                    // 폴백: IndicatorScale 적용 logical 높이를 현재 DPI로 스케일링.
                    double scale = _config.IndicatorScale;
                    labelH = DpiHelper.Scale(
                        (int)Math.Round(_config.LabelHeight * scale),
                        DpiHelper.GetScale(hMonitor));
                }
                int x = frame.Left;
                int y = frame.Top - labelH - SystemInputGapPx;
                if (y < workArea.Top) y = workArea.Top;
                return (x, y);
            }
            // 캐시도 없는 경우 → 일반 기본 위치로 폴스루
        }

        if (_config.DefaultIndicatorPosition is { } anchor)
            return ResolveAnchor(workArea, anchor);

        return (workArea.Right + DefaultConfig.DefaultIndicatorOffsetX,
                workArea.Top + DefaultConfig.DefaultIndicatorOffsetY);
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
        (int lastX, int lastY) = _engine.GetLastPosition();
        if (lastX == 0 && lastY == 0)
            return null;

        POINT pt = new(lastX, lastY);
        IntPtr hMonitor = User32.MonitorFromPoint(pt, Win32Constants.MONITOR_DEFAULTTONEAREST);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);

        // 4개 모서리까지의 맨해튼 거리를 각각 계산하여 최소값 선택.
        (Corner corner, int dx, int dy) best = (Corner.TopRight, 0, 0);
        long bestDist = long.MaxValue;

        void Consider(Corner c, int ax, int ay)
        {
            int dx = lastX - ax;
            int dy = lastY - ay;
            long dist = Math.Abs((long)dx) + Math.Abs((long)dy);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = (c, dx, dy);
            }
        }

        Consider(Corner.TopLeft, workArea.Left, workArea.Top);
        Consider(Corner.TopRight, workArea.Right, workArea.Top);
        Consider(Corner.BottomLeft, workArea.Left, workArea.Bottom);
        Consider(Corner.BottomRight, workArea.Right, workArea.Bottom);

        return new DefaultPositionConfig
        {
            Corner = best.corner,
            DeltaX = best.dx,
            DeltaY = best.dy,
        };
    }

    /// <summary>
    /// 현재 인디 위치에서 포그라운드 창의 가장 가까운 모서리를 찾아
    /// RelativePositionConfig로 환산. 트레이 "기본 위치 → 현재 위치로 설정"(창 기준 모드)에서 호출.
    /// <para>
    /// Delta 는 <b>논리 픽셀</b>(96 DPI 기준) 로 저장된다. 서로 다른 DPI 모니터 간 이동 시에도
    /// 창 모서리 대비 시각적 상대 위치가 보존되도록, 저장 시점 모니터의 DPI 스케일로 나눠 정규화한다.
    /// </para>
    /// 인디가 한 번도 표시된 적이 없거나 창 rect를 얻을 수 없으면 null.
    /// </summary>
    public static RelativePositionConfig? ComputeRelativeFromCurrentPosition(IntPtr hwndForeground)
    {
        (int lastX, int lastY) = _engine.GetLastPosition();
        if (lastX == 0 && lastY == 0) return null;
        if (hwndForeground == IntPtr.Zero) return null;
        if (!Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT frame)) return null;

        (Corner corner, int dx, int dy) best = (Corner.TopRight, 0, 0);
        long bestDist = long.MaxValue;

        void Consider(Corner c, int ax, int ay)
        {
            int cdx = lastX - ax;
            int cdy = lastY - ay;
            long dist = Math.Abs((long)cdx) + Math.Abs((long)cdy);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = (c, cdx, cdy);
            }
        }

        Consider(Corner.TopLeft, frame.Left, frame.Top);
        Consider(Corner.TopRight, frame.Right, frame.Top);
        Consider(Corner.BottomLeft, frame.Left, frame.Bottom);
        Consider(Corner.BottomRight, frame.Right, frame.Bottom);

        // 물리 px → 논리 px 변환. 창이 걸친 모니터의 DPI 스케일로 나눈다.
        // GetScale 이 실패해도 1.0 을 반환하므로 안전.
        double dpiScale = DpiHelper.GetScale(
            User32.MonitorFromWindow(hwndForeground, Win32Constants.MONITOR_DEFAULTTONEAREST));
        return new RelativePositionConfig
        {
            Corner = best.corner,
            DeltaX = (int)Math.Round(best.dx / dpiScale),
            DeltaY = (int)Math.Round(best.dy / dpiScale),
        };
    }

    /// <summary>
    /// RelativePositionConfig + 창 RECT → 절대 화면 좌표로 변환.
    /// ResolveAnchor의 창 기준 버전.
    /// <para>
    /// <paramref name="rel"/>.DeltaX/Y 는 논리 px 로 해석되며, 타겟 창의 모니터 DPI 스케일로
    /// 승산해 물리 px 로 변환 후 창 프레임 모서리에 가산한다. 이를 통해 서로 다른 DPI 모니터에서도
    /// 창 모서리 대비 시각적 상대 위치가 일정하게 유지된다.
    /// </para>
    /// </summary>
    public static (int x, int y) ResolveRelativePosition(RECT windowFrame, RelativePositionConfig rel, double dpiScale)
    {
        int physicalDx = DpiHelper.Scale(rel.DeltaX, dpiScale);
        int physicalDy = DpiHelper.Scale(rel.DeltaY, dpiScale);
        int x = rel.Corner is Corner.TopLeft or Corner.BottomLeft
            ? windowFrame.Left + physicalDx
            : windowFrame.Right + physicalDx;
        int y = rel.Corner is Corner.TopLeft or Corner.TopRight
            ? windowFrame.Top + physicalDy
            : windowFrame.Bottom + physicalDy;
        return (x, y);
    }

    /// <summary>
    /// 창 기준 모드의 기본 위치. 저장 위치 없는 앱에 사용.
    /// 시스템 입력 프로세스는 기존 GetDefaultPosition으로 위임.
    /// </summary>
    public static (int x, int y) GetDefaultRelativePosition(
        IntPtr hwndForeground, string processName,
        RelativePositionConfig? configDefault)
    {
        if (DefaultConfig.IsSystemInputProcess(processName))
            return GetDefaultPosition(hwndForeground, processName);

        if (hwndForeground == IntPtr.Zero
            || !Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT frame))
            return GetDefaultPosition(hwndForeground, processName);

        RelativePositionConfig anchor = configDefault ?? new RelativePositionConfig
        {
            Corner = DefaultConfig.DefaultRelativeCorner,
            DeltaX = DefaultConfig.DefaultRelativeOffsetX,
            DeltaY = DefaultConfig.DefaultRelativeOffsetY,
        };
        double dpiScale = DpiHelper.GetScale(
            User32.MonitorFromWindow(hwndForeground, Win32Constants.MONITOR_DEFAULTTONEAREST));
        return ResolveRelativePosition(frame, anchor, dpiScale);
    }

    // ================================================================
    // BuildStyle — ImeState + AppConfig → OverlayStyle
    //
    // ImeState 누출 금지 조건 충족: 이 메서드는 파사드 내부 유일한 state→string 변환 지점이며
    // Core 레이어(LayeredOverlayBase / OverlayStyle)는 ImeState를 전혀 참조하지 않는다.
    // ================================================================

    private static OverlayStyle BuildStyle(AppConfig config, ImeState state)
    {
        double scale = config.IndicatorScale;

        // 상태-라우팅된 색상/텍스트 합성
        (string bgHex, string fgHex, string labelText) = state switch
        {
            ImeState.Hangul => (config.HangulBg, config.HangulFg, config.HangulLabel),
            ImeState.English => (config.EnglishBg, config.EnglishFg, config.EnglishLabel),
            _ => (config.NonKoreanBg, config.NonKoreanFg, config.NonKoreanLabel),
        };

        // IndicatorScale 사전 곱셈 (DPI 미적용 logical px) — DPI는 엔진이 MulDiv로 곱함
        int labelWidthPx = (int)Math.Round(config.LabelWidth * scale);
        int labelHeightPx = (int)Math.Round(config.LabelHeight * scale);
        int borderRadiusPx = (int)Math.Round(config.LabelBorderRadius * scale);
        int borderWidthPx = (int)Math.Round(config.BorderWidth * scale);
        int paddingXPx = (int)Math.Round(DefaultConfig.LABEL_PADDING_X * scale);
        int fontSizePx = (int)Math.Round(config.FontSize * scale);

        return new OverlayStyle(
            FontFamily: config.FontFamily,
            FontSizeLogicalPx: fontSizePx,
            IsBold: config.FontWeight == FontWeight.Bold,
            LabelWidthLogicalPx: labelWidthPx,
            LabelHeightLogicalPx: labelHeightPx,
            BorderRadiusLogicalPx: borderRadiusPx,
            BorderWidthLogicalPx: borderWidthPx,
            PaddingXLogicalPx: paddingXPx,
            BgHex: bgHex,
            FgHex: fgHex,
            BorderHex: config.BorderColor,
            LabelText: labelText,
            CapsLockOn: _capsLockOn,
            MeasureLabels: (config.HangulLabel, config.EnglishLabel, config.NonKoreanLabel)
        );
    }

    // ================================================================
    // OnRenderToDib — 실제 GDI 그리기 (LayeredOverlayBase 콜백)
    //
    // 엔진이 DIB 픽셀을 0-클리어하고 _currentFont를 HDC에 SelectObject한 상태로 호출한다.
    // 콜백은 배경 RoundRect → (옵션) 보더 RoundRect → DrawTextW 순서로 그리면 된다.
    // Premultiplied alpha 후처리와 UpdateLayeredWindow는 콜백 반환 후 엔진이 수행.
    // ================================================================

    private static (int w, int h) OnRenderToDib(IntPtr hdc, OverlayStyle style, OverlayMetrics metrics)
    {
        int w = metrics.ScaledWidth;
        int h = metrics.ScaledHeight;

        // 1. NULL_PEN 선택 (RoundRect 1px 검은색 테두리 제거)
        IntPtr oldPen = Gdi32.SelectObject(hdc, Gdi32.GetStockObject(Win32Constants.NULL_PEN));

        // 2. 배경 RoundRect — hBrush 는 try/finally 로 예외 경로에서도 DeleteObject 보장
        uint bgColor = ColorHelper.HexToColorRef(style.BgHex);
        IntPtr hBrush = Gdi32.CreateSolidBrush(bgColor);
        try
        {
            IntPtr oldBrush = Gdi32.SelectObject(hdc, hBrush);
            int radius = metrics.ScaledBorderRadius;
            Gdi32.RoundRect(hdc, 0, 0, w, h, radius, radius);
            Gdi32.SelectObject(hdc, oldBrush);

            // 3. 보더 (borderWidth > 0) — hBorderPen 도 중첩 try/finally 로 보호
            int borderW = metrics.ScaledBorderWidth;
            if (borderW > 0)
            {
                uint borderColor = ColorHelper.HexToColorRef(style.BorderHex);
                IntPtr hBorderPen = Gdi32.CreatePen(Win32Constants.PS_SOLID, borderW, borderColor);
                try
                {
                    IntPtr hNullBrush = Gdi32.GetStockObject(Win32Constants.NULL_BRUSH);
                    IntPtr oldBorderPen = Gdi32.SelectObject(hdc, hBorderPen);
                    IntPtr oldBorderBrush = Gdi32.SelectObject(hdc, hNullBrush);

                    int halfBorder = borderW / 2;
                    Gdi32.RoundRect(hdc, halfBorder, halfBorder, w - halfBorder, h - halfBorder, radius, radius);

                    Gdi32.SelectObject(hdc, oldBorderBrush);
                    Gdi32.SelectObject(hdc, oldBorderPen);
                }
                finally
                {
                    Gdi32.DeleteObject(hBorderPen);
                }
            }

            // 4. 텍스트 (폰트는 엔진이 사전 SelectObject)
            int oldBkMode = Gdi32.SetBkMode(hdc, Win32Constants.TRANSPARENT);
            uint fgColor = ColorHelper.HexToColorRef(style.FgHex);
            uint oldTextColor = Gdi32.SetTextColor(hdc, fgColor);

            // textRect를 위로 vOffset만큼 이동 (Top/Bottom 동시) — 높이는 보존되므로
            // DT_VCENTER가 사각형 안에서 셀을 정상 중앙 정렬하고, 사각형 자체가 위로 이동한 만큼
            // 글리프 시각 중심이 DIB 중앙에 정확히 맞춰진다. vOffset의 산출은 LayeredOverlayBase.EnsureFont
            // 의 GetTextMetricsW 분기 참조 ((tmInternalLeading - tmDescent) / 2).
            int vOffset = metrics.TextVCenterOffsetPx;
            var textRect = new RECT { Left = 0, Top = -vOffset, Right = w, Bottom = h - vOffset };
            User32.DrawTextW(hdc, style.LabelText, style.LabelText.Length, ref textRect,
                Win32Constants.DT_CENTER | Win32Constants.DT_VCENTER | Win32Constants.DT_SINGLELINE);

            Gdi32.SetTextColor(hdc, oldTextColor);
            Gdi32.SetBkMode(hdc, oldBkMode);

            // 5. CAPS LOCK 막대 (좌우 세로 띠, fg 색상 재사용) — 상수는 클래스 헤드의 Caps* 참조
            //    - topY/bottomY: borderRadius만큼 수직 인셋 → 모서리 라운드와 겹치지 않음
            //    - leftX:  max(borderWidth, CapsLockBarInsetLogicalPx) 수평 인셋 (보더가 있으면 보더 안쪽)
            //    - rightX: leftX 대칭 위치에 +CapsLockRightCompensationPx 추가 시각 보정
            //    - 병리 설정(LabelHeight < 2*BorderRadius) 또는 w가 너무 좁은 경우 silent skip
            if (style.CapsLockOn)
            {
                IntPtr hCapsBrush = Gdi32.CreateSolidBrush(fgColor);
                try
                {
                    int barW = Math.Max(1, DpiHelper.Scale(CapsLockBarWidthLogicalPx, metrics.DpiScale));
                    int inset = Math.Max(metrics.ScaledBorderWidth, DpiHelper.Scale(CapsLockBarInsetLogicalPx, metrics.DpiScale));
                    int leftX = inset;
                    int rightX = w - inset - barW - CapsLockRightCompensationPx;
                    int topY = metrics.ScaledBorderRadius;
                    int bottomY = h - metrics.ScaledBorderRadius;
                    if (bottomY > topY && rightX >= leftX + barW)
                    {
                        var leftRect = new RECT { Left = leftX, Top = topY, Right = leftX + barW, Bottom = bottomY };
                        var rightRect = new RECT { Left = rightX, Top = topY, Right = rightX + barW, Bottom = bottomY };
                        User32.FillRect(hdc, ref leftRect, hCapsBrush);
                        User32.FillRect(hdc, ref rightRect, hCapsBrush);
                    }
                }
                finally
                {
                    Gdi32.DeleteObject(hCapsBrush);
                }
            }
        }
        finally
        {
            Gdi32.DeleteObject(hBrush);
            Gdi32.SelectObject(hdc, oldPen);
        }

        return (w, h);
    }
}
