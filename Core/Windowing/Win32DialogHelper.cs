using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KoEnVue.Core.Dpi;
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
    /// <summary>기본 다이얼로그 폰트 크기 (pt). Windows 시스템 9pt 표준 — 폰트 패밀리는
    /// 호출자가 결정 (P6 — Core 는 한국어 폰트 어휘를 알지 않음).</summary>
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
    /// 다이얼로그용 9pt SafeFontHandle 을 생성한다. 폰트 패밀리는 App 레이어가 결정하며
    /// (P6 — Core 는 한국어 폰트 어휘를 모름), 호출자가 명시적으로 전달한다.
    /// 호출자는 <c>using var hFont = Win32DialogHelper.CreateDialogFont(dpiY, family);</c>
    /// 스코프로 폰트 수명을 모달 루프 + DestroyWindow 구간에 고정해야 한다
    /// (Risk 3 — early release 는 DrawTextW crash).
    /// </summary>
    public static SafeFontHandle CreateDialogFont(uint dpiY, string fontFamily)
    {
        int fontHeight = CalculateFontHeightPx(dpiY);
        return new SafeFontHandle(
            Gdi32.CreateFontW(fontHeight, 0, 0, 0, Win32Constants.FW_NORMAL,
                0, 0, 0, Win32Constants.DEFAULT_CHARSET,
                Win32Constants.OUT_TT_PRECIS, Win32Constants.CLIP_DEFAULT_PRECIS,
                Win32Constants.CLEARTYPE_QUALITY, Win32Constants.DEFAULT_PITCH,
                fontFamily),
            ownsHandle: true);
    }

    /// <summary>
    /// 필드 입력 검증 실패 시 공통 에러 표시: 안내 MessageBox(자체 루프라 RunExternal 가드) +
    /// 해당 입력 컨트롤로 포커스 이동 + 전체 텍스트 선택(EM_SETSEL 0..-1).
    /// <para>
    /// MessageBoxW 는 자체 메시지 루프를 돌려 ModalDialogLoop.Run 으로 감쌀 수 없으므로
    /// RunExternal 로 IsActive 가드만 씌워 박스가 열린 동안 감지 스레드 사이드-이펙트를 억제한다.
    /// EM_SETSEL 은 EDIT 컨트롤 전용이므로, 호출자는 <paramref name="hwndField"/> 가 EDIT 일 때만
    /// 이 헬퍼를 사용한다 (체크박스/콤보 등은 별도 처리). 스크롤 다이얼로그의 ScrollIntoView 같은
    /// 컨트롤별 선행 동작은 호출자가 본 호출 전후에 직접 수행한다.
    /// </para>
    /// </summary>
    public static void ShowFieldError(IntPtr hwndOwner, IntPtr hwndField, string message, string title)
    {
        ModalDialogLoop.RunExternal(hwndOwner, () =>
            User32.MessageBoxW(hwndOwner, message, title, uType: Win32Constants.MB_OK));
        User32.SetFocus(hwndField);
        User32.SendMessageW(hwndField, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
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
        // GetWorkArea 는 GetMonitorInfoW 실패 시 primary 모니터로 1회 폴백한다 — hMonitor 무효/조회
        // 실패 시 rcWork 가 (0,0,0,0) 으로 떨어져 다이얼로그가 화면 좌상단(0,0)에 박히는 것을 방지.
        RECT rcWork = DpiHelper.GetWorkArea(hMonitor);

        int cx, cy;
        if (anchor is POINT pt)
        {
            cx = pt.X;
            cy = pt.Y;
        }
        else
        {
            cx = (rcWork.Left + rcWork.Right - dlgWidth) / 2;
            cy = (rcWork.Top + rcWork.Bottom - dlgHeight) / 2;
        }

        if (cx + dlgWidth > rcWork.Right) cx = rcWork.Right - dlgWidth;
        if (cy + dlgHeight > rcWork.Bottom) cy = rcWork.Bottom - dlgHeight;
        if (cx < rcWork.Left) cx = rcWork.Left;
        if (cy < rcWork.Top) cy = rcWork.Top;

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
                LogProvider.Sink?.Debug($"Window class '{className}' already registered (reusing)");
            }
            else
            {
                LogProvider.Sink?.Error($"RegisterClassExW failed for '{className}': error={err}");
            }
        }
        else
        {
            LogProvider.Sink?.Debug($"Window class registered: '{className}' atom={atom}");
        }
        return atom;
    }
}
