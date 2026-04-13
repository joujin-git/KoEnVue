using KoEnVue.Native;

namespace KoEnVue.Utils;

/// <summary>
/// Win32 모달 다이얼로그에서 반복 사용되는 DPI-aware 메트릭 계산 유틸.
/// P4 공통모듈: CleanupDialog, ScaleDialog, SettingsDialog 세 곳이 동일한
/// 비-클라이언트 높이·폭 + 9pt 시스템 폰트 높이 계산을 중복 구현해 왔다.
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
    public static void ApplyFont(IntPtr hwnd, IntPtr hFont)
    {
        User32.SendMessageW(hwnd, Win32Constants.WM_SETFONT, hFont, (IntPtr)1);
    }
}
