using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// Win32 모달 다이얼로그에서 반복 사용되는 DPI-aware 메트릭 계산 + 윈도우 클래스 등록 유틸.
/// P4 공통모듈: CleanupDialog, ScaleDialog, SettingsDialog 세 곳이 동일한
/// 비-클라이언트 높이·폭 + 9pt 시스템 폰트 높이 계산을 중복 구현해 왔다. 그 위에
/// 윈도우 클래스 등록(hCursor=IDC_ARROW 강제 + 단일 로깅 경로)도 같은 단일 진입점으로
/// 통합해, hCursor 누락에서 비롯되는 IDC_APPSTARTING 폴백 결함이 다시 발생하지 않도록 한다.
///
/// 주의: Overlay의 `Kernel32.MulDiv(scaledFontSize, dpiY, 72)` 경로는 별개 케이스다.
/// 오버레이 폰트는 config.FontSize 가변이라 MulDiv가 더 정확하고, 여기 다이얼로그의
/// "9pt 시스템 폰트 고정 계산"과 의미가 다르므로 공통화하지 않는다.
/// </summary>
internal static class Win32DialogHelper
{
    /// <summary>기본 다이얼로그 폰트 크기 (pt). 맑은 고딕 9pt 시스템 기본값.</summary>
    public const double DefaultDialogFontPointSize = 9.0;

    /// <summary>1인치당 포인트 수 (타이포그래피 표준).</summary>
    private const double PointsPerInch = 72.0;

    /// <summary>
    /// 캡션 + 2*FIXEDFRAME + 2*PADDEDBORDER → 다이얼로그 비-클라이언트 높이.
    /// WS_CAPTION + WS_SYSMENU 스타일의 고정 크기 다이얼로그 기준.
    /// </summary>
    public static int CalculateNonClientHeight(uint rawDpi)
    {
        return User32.GetSystemMetricsForDpi(Win32Constants.SM_CYCAPTION, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CYFIXEDFRAME, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, rawDpi);
    }

    /// <summary>
    /// 2*SM_CXFIXEDFRAME + 2*SM_CXPADDEDBORDER → 다이얼로그 비-클라이언트 폭.
    /// 수평 계산이므로 SM_CYFIXEDFRAME(수직) 대신 SM_CXFIXEDFRAME을 사용한다.
    /// 대부분 테마에서 수평·수직 값이 동일하지만 API 의미 일관성을 위해 구분.
    /// </summary>
    public static int CalculateNonClientWidth(uint rawDpi)
    {
        return 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CXFIXEDFRAME, rawDpi)
            + 2 * User32.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, rawDpi);
    }

    /// <summary>
    /// 지정한 포인트 크기의 GDI 음수 높이(-(point * dpi / 72))를 반환.
    /// CreateFontW 의 lfHeight 파라미터용. 기본값은 9pt 시스템 폰트.
    /// </summary>
    public static int CalculateFontHeightPx(uint dpiY, double pointSize = DefaultDialogFontPointSize)
    {
        return -(int)Math.Round(pointSize * dpiY / PointsPerInch);
    }

    /// <summary>
    /// 지정 윈도우에 WM_SETFONT 메시지를 보내 시스템 폰트를 적용한다.
    /// wParam = hFont, lParam = TRUE (다시 그리기 요청).
    /// CleanupDialog/ScaleInputDialog/SettingsDialog 공용 단일 라인 헬퍼.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyFont(IntPtr hwnd, IntPtr hFont)
    {
        User32.SendMessageW(hwnd, Win32Constants.WM_SETFONT, hFont, (IntPtr)1);
    }

    /// <summary>
    /// 다이얼로그용 맑은 고딕 9pt SafeFontHandle을 생성한다.
    /// CleanupDialog/ScaleInputDialog/SettingsDialog 세 곳이 동일하게 사용하던
    /// 9줄짜리 CreateFontW 호출 + SafeFontHandle 래핑 보일러플레이트를 흡수한다.
    /// 호출자는 `using var hFont = Win32DialogHelper.CreateDialogFont(dpiY);` 스코프로
    /// 폰트 수명을 모달 루프 + DestroyWindow 구간에 고정해야 한다 (Risk 3 — early release는 DrawTextW crash).
    /// </summary>
    public static SafeFontHandle CreateDialogFont(uint dpiY)
    {
        int fontHeight = CalculateFontHeightPx(dpiY);
        return new SafeFontHandle(
            Gdi32.CreateFontW(fontHeight, 0, 0, 0, Win32Constants.FW_NORMAL,
                0, 0, 0, Win32Constants.DEFAULT_CHARSET,
                Win32Constants.OUT_TT_PRECIS, Win32Constants.CLIP_DEFAULT_PRECIS,
                Win32Constants.CLEARTYPE_QUALITY, Win32Constants.DEFAULT_PITCH,
                "맑은 고딕"),
            ownsHandle: true);
    }

    /// <summary>
    /// 다이얼로그 좌측-상단 스크린 좌표를 계산한다. 세 다이얼로그가 공통으로 하던
    /// `GetMonitorInfoW(rcWork) → center 또는 cursor 기준 + 모니터 경계 클램프` 계산을 흡수한다.
    ///
    /// <paramref name="anchor"/>:
    ///   null  → 모니터 작업 영역 정중앙 (CleanupDialog, SettingsDialog 패턴)
    ///   not null → 해당 스크린 좌표에 좌측-상단을 두고 작업 영역 경계 안으로 클램프
    ///              (ScaleInputDialog 의 "커서 위치 근처" 패턴)
    ///
    /// <paramref name="hMonitor"/> 는 호출자가 이미 조회한 모니터 핸들을 재사용한다 —
    /// 헬퍼가 내부적으로 MonitorFromPoint 를 다시 부르면 드래그-다이얼로그 or 다중-모니터
    /// 시나리오에서 호출자의 모니터 선택 의도와 달라질 수 있기 때문.
    /// </summary>
    public static (int cx, int cy) CalculateDialogPosition(
        IntPtr hMonitor, int dlgWidth, int dlgHeight, POINT? anchor = null)
    {
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMonitor, ref mi);

        int cx, cy;
        if (anchor is POINT pt)
        {
            cx = pt.X;
            cy = pt.Y;
        }
        else
        {
            cx = (mi.rcWork.Left + mi.rcWork.Right - dlgWidth) / 2;
            cy = (mi.rcWork.Top + mi.rcWork.Bottom - dlgHeight) / 2;
        }

        if (cx + dlgWidth > mi.rcWork.Right) cx = mi.rcWork.Right - dlgWidth;
        if (cy + dlgHeight > mi.rcWork.Bottom) cy = mi.rcWork.Bottom - dlgHeight;
        if (cx < mi.rcWork.Left) cx = mi.rcWork.Left;
        if (cy < mi.rcWork.Top) cy = mi.rcWork.Top;

        return (cx, cy);
    }

    /// <summary>
    /// 표준 윈도우 클래스를 등록한다 — <c>hCursor</c> 는 항상 <c>IDC_ARROW</c> 로 박힌다.
    /// <para>
    /// <c>hCursor=NULL</c> 이면 첫 실행 후 OS 의 startup grace period(<c>HourglassWaitTime</c>
    /// 레지스트리 기본 ~5 초) 동안 클라이언트 영역에 <c>IDC_APPSTARTING</c>(화살표+모래시계)
    /// 폴백이 노출된다. 모든 데스크톱 윈도우 클래스 등록을 이 단일 진입점으로 통일해
    /// hCursor 누락성 결함이 재발하지 않도록 구조적으로 차단한다 — P4 (No duplicate impl).
    /// </para>
    /// <para>
    /// <paramref name="hbrBackground"/> 는 옵셔널.
    /// 다이얼로그처럼 GDI 배경 그리기가 필요한 클래스는 <c>(IntPtr)(COLOR_BTNFACE + 1)</c>
    /// 형태(시스템 컬러 인덱스 + 1)로 전달한다. 메시지 전용 (0×0 hidden) 윈도우나
    /// layered overlay (<c>WS_EX_LAYERED</c>, <c>WM_ERASEBKGND</c> 미수신) 는 default(NULL) 로 둔다.
    /// </para>
    /// </summary>
    /// <returns>등록된 클래스의 atom, 실패 시 0.</returns>
    internal static unsafe ushort RegisterStandardClass(
        string className,
        delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> wndProc,
        IntPtr hbrBackground = default)
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)wndProc,
            hCursor = User32.LoadCursorW(IntPtr.Zero, Win32Constants.IDC_ARROW),
            hbrBackground = hbrBackground,
            lpszClassName = className,
        };
        ushort atom = User32.RegisterClassExW(ref wc);
        if (atom == 0)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err == Win32Constants.ERROR_CLASS_ALREADY_EXISTS)
            {
                // 같은 프로세스에 이미 등록된 클래스 — 첫 등록이 살아있어 CreateWindowExW 가 정상
                // 동작. 결함이 아닌 idempotent 경로라 Debug 레벨 + "failed" 단어 회피 (사용자가 로그를
                // 봤을 때 불필요한 불안 유발 방지). 다이얼로그 Show() 가 매 호출마다 RegisterClassExW
                // 를 호출하는 구조라 두 번째 이상의 오픈 시 자연스럽게 이 경로를 탄다.
                Logger.Debug($"Window class '{className}' already registered (reusing)");
            }
            else
            {
                Logger.Error($"RegisterClassExW failed for '{className}': error={err}");
            }
        }
        else
        {
            Logger.Debug($"Window class registered: '{className}' atom={atom}");
        }
        return atom;
    }
}
