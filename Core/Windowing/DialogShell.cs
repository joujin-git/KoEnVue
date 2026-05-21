using KoEnVue.Core.Dpi;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// 모달 다이얼로그 라이프사이클 공통 골격 — reentry guard + DPI/font/class 등록 +
/// CreateWindowExW + ShowWindow + ModalDialogLoop + DestroyWindow 를 단일 진입점에 집약한다.
/// CleanupDialog / ScaleInputDialog / SettingsDialog 세 다이얼로그가 동일한 ~50줄
/// 프롤로그 + ~15줄 에필로그 보일러플레이트를 반복하던 것을 흡수해, hCursor 누락이나
/// EnableWindow 비활성화 순서 같은 결함을 단일 코드로 차단한다 — P4 (no duplicate impl).
///
/// <para>
/// 각 다이얼로그는 자기 책임만 콜백으로 전달한다:
/// <list type="bullet">
///   <item><c>measureDlgHeight</c> — DPI/nonClient 메트릭으로부터 다이얼로그 전체 높이 계산</item>
///   <item><c>buildChildren</c> — 자식 컨트롤 생성 (메인 윈도우 hwnd 와 폰트는 ctx 로 받음)</item>
///   <item><c>onAfterShow</c> — ShowWindow 직후 추가 초기화 (SetFocus / EM_SETSEL 등)</item>
/// </list>
/// WndProc 와 모달 종료 플래그는 호출자가 소유한다 (다이얼로그별 정적 상태 참조가 다름).
/// </para>
/// </summary>
internal readonly record struct DialogShellMetrics(
    IntPtr HMonitor,
    POINT CursorPos,
    double DpiScale,
    uint DpiY,
    uint RawDpi,
    int NonClientH,
    int NonClientW,
    int Pad,
    int DlgWidth)
{
    public int Scale(int logical) => DpiHelper.Scale(logical, DpiScale);
}

internal sealed class DialogShellContext
{
    public required IntPtr HwndOwner { get; init; }
    public required IntPtr HwndDialog { get; init; }
    public required IntPtr HFont { get; init; }
    public required int DlgHeight { get; init; }
    public required DialogShellMetrics Metrics { get; init; }

    public IntPtr HMonitor => Metrics.HMonitor;
    public double DpiScale => Metrics.DpiScale;
    public uint DpiY => Metrics.DpiY;
    public uint RawDpi => Metrics.RawDpi;
    public int Pad => Metrics.Pad;
    public int DlgWidth => Metrics.DlgWidth;
    public int NonClientH => Metrics.NonClientH;
    public int NonClientW => Metrics.NonClientW;
    public int ClientW => Metrics.DlgWidth - Metrics.NonClientW;
    public int ClientH => DlgHeight - Metrics.NonClientH;
    public int Scale(int logical) => Metrics.Scale(logical);
}

internal static class DialogShell
{
    /// <summary>
    /// 다이얼로그 외곽 표준 패딩 (96 DPI 기준 logical px). 세 다이얼로그가 동일하게
    /// 사용하던 <c>DlgPadding / DlgPad / ScaleDlgPad = 16</c> 매직 리터럴의 단일 진실원.
    /// </summary>
    public const int StandardPadLogical = 16;

    /// <summary>
    /// 모달 다이얼로그를 표시한다. <see cref="ModalDialogLoop.IsActive"/> 가 true 면 기존
    /// 활성 다이얼로그로 포커스만 복원하고 <c>false</c> 를 반환한다 (호출자는 결과 수집 없이 조기 반환).
    /// 정상 실행 후에는 <c>true</c> 를 반환하며, 결과 (확인/취소 여부 + 입력값) 는 호출자가
    /// 자체 정적 필드를 통해 수집한다.
    /// </summary>
    /// <param name="hwndOwner">모달 소유자 (= 메인 윈도우) HWND.</param>
    /// <param name="className">윈도우 클래스 이름.</param>
    /// <param name="wndProc">UnmanagedCallersOnly WndProc 함수 포인터.</param>
    /// <param name="title">캡션 텍스트.</param>
    /// <param name="dlgLogicalWidth">96 DPI 기준 다이얼로그 너비 (logical px).</param>
    /// <param name="measureDlgHeight">DPI 메트릭으로부터 DPI-스케일된 다이얼로그 높이를 계산.</param>
    /// <param name="useCursorAnchor">true=커서 위치를 좌측-상단으로 배치 (ScaleInputDialog 패턴),
    ///                                false=작업 영역 정중앙 (Settings/Cleanup 패턴).</param>
    /// <param name="bringToForeground">ShowWindow 직후 SetForegroundWindow 호출 여부.</param>
    /// <param name="buildChildren">CreateWindowExW 직후 자식 컨트롤을 생성한다.</param>
    /// <param name="onAfterShow">ShowWindow + SetForegroundWindow 직후의 추가 초기화 (옵셔널).</param>
    /// <param name="isClosedFlag">WndProc 가 true 로 전환하면 모달 루프 종료.</param>
    public static unsafe bool Run(
        IntPtr hwndOwner,
        string className,
        delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> wndProc,
        string title,
        int dlgLogicalWidth,
        Func<DialogShellMetrics, int> measureDlgHeight,
        bool useCursorAnchor,
        bool bringToForeground,
        Action<DialogShellContext> buildChildren,
        Action<DialogShellContext>? onAfterShow,
        ref bool isClosedFlag)
    {
        // 재진입 가드: 기존 모달이 있으면 그 창으로 포커스만 복원하고 false 반환.
        if (ModalDialogLoop.IsActive)
        {
            User32.SetForegroundWindow(ModalDialogLoop.ActiveDialog);
            return false;
        }

        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTONEAREST);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);
        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);
        int nonClientH = Win32DialogHelper.CalculateNonClientHeight(rawDpi);
        int nonClientW = Win32DialogHelper.CalculateNonClientWidth(rawDpi);
        int pad = DpiHelper.Scale(StandardPadLogical, dpiScale);
        int dlgWidth = DpiHelper.Scale(dlgLogicalWidth, dpiScale);

        var metrics = new DialogShellMetrics(
            HMonitor: hMon,
            CursorPos: cursorPt,
            DpiScale: dpiScale,
            DpiY: dpiY,
            RawDpi: rawDpi,
            NonClientH: nonClientH,
            NonClientW: nonClientW,
            Pad: pad,
            DlgWidth: dlgWidth);

        int dlgHeight = measureDlgHeight(metrics);

        // 폰트 생명은 모달 루프 + DestroyWindow 구간 전체. 호출자가 SafeFontHandle 을
        // using 으로 쥐고 있던 패턴을 셸이 흡수.
        using var hFont = Win32DialogHelper.CreateDialogFont(dpiY);

        Win32DialogHelper.RegisterStandardClass(
            className, wndProc,
            (IntPtr)(Win32Constants.COLOR_BTNFACE + 1));

        var (cx, cy) = Win32DialogHelper.CalculateDialogPosition(
            hMon, dlgWidth, dlgHeight, useCursorAnchor ? cursorPt : null);

        IntPtr hwndDlg = User32.CreateWindowExW(0, className, title,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            hwndOwner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwndDlg == IntPtr.Zero)
            return false;

        var ctx = new DialogShellContext
        {
            HwndOwner = hwndOwner,
            HwndDialog = hwndDlg,
            HFont = hFont.DangerousGetHandle(),
            DlgHeight = dlgHeight,
            Metrics = metrics,
        };

        try
        {
            buildChildren(ctx);
            User32.ShowWindow(hwndDlg, Win32Constants.SW_SHOW);
            if (bringToForeground)
                User32.SetForegroundWindow(hwndDlg);
            onAfterShow?.Invoke(ctx);
            ModalDialogLoop.Run(hwndDlg, hwndOwner, ref isClosedFlag);
        }
        finally
        {
            User32.DestroyWindow(hwndDlg);
        }
        return true;
    }

    /// <summary>
    /// 표준 WM_COMMAND 분기 (IDOK / IDCANCEL + 다이얼로그-고유 OK/Cancel 버튼 ID 동시 수락).
    /// IDOK 도달 시 <paramref name="tryCommit"/> 가 null 이거나 true 면 결과를 OK 로 확정한다.
    /// IsDialogMessageW 가 Enter→IDOK(1), ESC→IDCANCEL(2) 로 변환해 보내는 경우와
    /// 사용자가 버튼을 직접 클릭하는 경우 양쪽을 같은 분기로 처리한다.
    /// </summary>
    /// <returns>표준 명령으로 처리되었으면 true, 그 외면 false (호출자가 자체 분기 처리).</returns>
    public static bool HandleStandardCommands(
        int wmCommandId, int idOk, int idCancel,
        ref bool dlgResult, ref bool dlgClosed,
        Func<bool>? tryCommit = null)
    {
        if (wmCommandId == idOk || wmCommandId == Win32Constants.IDOK)
        {
            if (tryCommit == null || tryCommit())
            {
                dlgResult = true;
                dlgClosed = true;
            }
            return true;
        }
        if (wmCommandId == idCancel || wmCommandId == Win32Constants.IDCANCEL)
        {
            dlgResult = false;
            dlgClosed = true;
            return true;
        }
        return false;
    }
}
