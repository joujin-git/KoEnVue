using System.Runtime.InteropServices;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// 인디케이터 배율 직접 입력용 Win32 모달 다이얼로그.
/// 커서 위치 근처에 표시되며 [1.0, 5.0] 범위 외 입력은 에러로 반려한다.
/// Tray.cs에서 분리 (Wave 1-D).
/// </summary>
internal static class ScaleInputDialog
{
    // ================================================================
    // 범위/허용오차 상수 (Tray의 서브메뉴와 동일)
    // ================================================================
    public const double ScaleMinValue = 1.0;
    public const double ScaleMaxValue = 5.0;
    public const double ScaleTolerance = 0.001;

    // 레이아웃 상수 (96 DPI 기준)
    private const int ScaleDlgWidth = 320;
    private const int ScaleDlgPad = 16;
    private const int ScaleDlgLabelH = 20;
    private const int ScaleDlgEditH = 26;
    private const int ScaleDlgHintH = 18;
    private const int ScaleDlgBtnW = 80;
    private const int ScaleDlgBtnH = 28;
    private const int ScaleDlgGap = 8;

    // 컨트롤 ID
    private const int IDC_SCALE_EDIT = 6001;
    private const int IDC_SCALE_OK = 6002;
    private const int IDC_SCALE_CANCEL = 6003;

    // Win32 윈도우 클래스 이름
    private const string DlgClassName = "KoEnVueScaleDlg";

    // ================================================================
    // 다이얼로그 모달 상태
    // ================================================================
    private static bool _scaleDlgResult;
    private static bool _scaleDlgClosed;
    private static IntPtr _hwndScaleDlg;
    private static IntPtr _hwndScaleEdit;
    private static double _scaleDlgParsedValue;

    /// <summary>배율이 정수에 매우 가까운지 확인 (1e-3 tolerance).</summary>
    public static bool IsIntegerScale(double scale) =>
        Math.Abs(scale - Math.Round(scale)) < ScaleTolerance;

    /// <summary>
    /// 배율 직접 입력 대화상자. 커서 위치(서브메뉴 직전 클릭 좌표)에 모달로 표시.
    /// 확인 클릭 시 입력값 유효성 검사: 숫자 파싱 실패 또는 [1.0, 5.0] 범위 밖이면
    /// MessageBox로 안내 후 대화상자 유지. 취소/ESC → null.
    /// </summary>
    public static unsafe double? Show(IntPtr hwndMain, double initialValue)
    {
        _scaleDlgResult = false;
        _scaleDlgClosed = false;
        _scaleDlgParsedValue = 0;

        // 커서 위치 기준 모니터 DPI/work area
        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTONEAREST);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);

        int pad = DpiHelper.Scale(ScaleDlgPad, dpiScale);
        int labelH = DpiHelper.Scale(ScaleDlgLabelH, dpiScale);
        int editH = DpiHelper.Scale(ScaleDlgEditH, dpiScale);
        int hintH = DpiHelper.Scale(ScaleDlgHintH, dpiScale);
        int btnW = DpiHelper.Scale(ScaleDlgBtnW, dpiScale);
        int btnH = DpiHelper.Scale(ScaleDlgBtnH, dpiScale);
        int gap = DpiHelper.Scale(ScaleDlgGap, dpiScale);
        int dlgWidth = DpiHelper.Scale(ScaleDlgWidth, dpiScale);

        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);
        int nonClientH = Win32DialogHelper.CalculateNonClientHeight(rawDpi);

        int dlgHeight = nonClientH
            + pad
            + labelH + gap
            + editH + gap
            + hintH + pad
            + btnH
            + pad;

        // UI 폰트 (맑은 고딕 9pt, DPI 스케일) — using 스코프 종료 시 자동 DeleteObject
        int fontHeight = Win32DialogHelper.CalculateFontHeightPx(dpiY);
        using var hFont = new SafeFontHandle(
            Gdi32.CreateFontW(fontHeight, 0, 0, 0, Win32Constants.FW_NORMAL,
                0, 0, 0, Win32Constants.DEFAULT_CHARSET,
                Win32Constants.OUT_TT_PRECIS, Win32Constants.CLIP_DEFAULT_PRECIS,
                Win32Constants.CLEARTYPE_QUALITY, Win32Constants.DEFAULT_PITCH,
                "맑은 고딕"),
            ownsHandle: true);
        IntPtr hFontRaw = hFont.DangerousGetHandle();

        // 다이얼로그 클래스 등록 (중복 호출은 무시됨)
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ScaleDlgProc,
            lpszClassName = DlgClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref wc);

        // 대화상자 좌표: 커서 위치 기준, 모니터 work area 안으로 클램프
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMon, ref mi);

        int cx = cursorPt.X;
        int cy = cursorPt.Y;
        if (cx + dlgWidth > mi.rcWork.Right) cx = mi.rcWork.Right - dlgWidth;
        if (cy + dlgHeight > mi.rcWork.Bottom) cy = mi.rcWork.Bottom - dlgHeight;
        if (cx < mi.rcWork.Left) cx = mi.rcWork.Left;
        if (cy < mi.rcWork.Top) cy = mi.rcWork.Top;

        _hwndScaleDlg = User32.CreateWindowExW(0, DlgClassName, I18n.ScaleDialogTitle,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            hwndMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwndScaleDlg == IntPtr.Zero)
        {
            return null;
        }

        int borderW = DpiHelper.Scale(16, dpiScale);
        int contentW = dlgWidth - pad * 2 - borderW;

        int y = pad;

        // 안내 레이블 ("배율 (1.0 ~ 5.0):")
        IntPtr hwndLabel = User32.CreateWindowExW(0, "STATIC", I18n.ScaleDialogPrompt,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, labelH,
            _hwndScaleDlg, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndLabel, hFontRaw);
        y += labelH + gap;

        // EDIT 박스 — 현재 값으로 미리 채움
        string initialText = initialValue.ToString("0.#");
        _hwndScaleEdit = User32.CreateWindowExW(0, "EDIT", initialText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_BORDER
                | Win32Constants.WS_TABSTOP | Win32Constants.ES_LEFT | Win32Constants.ES_AUTOHSCROLL,
            pad, y, contentW, editH,
            _hwndScaleDlg, (IntPtr)IDC_SCALE_EDIT, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(_hwndScaleEdit, hFontRaw);
        y += editH + gap;

        // 힌트 레이블
        IntPtr hwndHint = User32.CreateWindowExW(0, "STATIC", I18n.ScaleDialogHint,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, hintH,
            _hwndScaleDlg, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndHint, hFontRaw);
        y += hintH + pad;

        // 버튼 — 오른쪽 정렬
        int btnAreaWidth = btnW * 2 + gap;
        int btnX = dlgWidth - borderW - pad - btnAreaWidth;

        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogOk,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP
                | Win32Constants.BS_DEFPUSHBUTTON,
            btnX, y, btnW, btnH,
            _hwndScaleDlg, (IntPtr)IDC_SCALE_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, hFontRaw);

        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogCancel,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + gap, y, btnW, btnH,
            _hwndScaleDlg, (IntPtr)IDC_SCALE_CANCEL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndCancel, hFontRaw);

        // 모달 표시 + EDIT 포커스 + 텍스트 전체 선택
        User32.EnableWindow(hwndMain, false);
        User32.ShowWindow(_hwndScaleDlg, Win32Constants.SW_SHOW);
        User32.SetForegroundWindow(_hwndScaleDlg);
        User32.SetFocus(_hwndScaleEdit);
        User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));

        // 모달 루프 — IsDialogMessageW로 Tab/Enter/ESC 기본 처리
        while (!_scaleDlgClosed)
        {
            int ret = User32.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0);
            if (ret <= 0) break;
            if (!User32.IsDialogMessageW(_hwndScaleDlg, ref msg))
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }

        double? result = _scaleDlgResult ? _scaleDlgParsedValue : null;

        User32.EnableWindow(hwndMain, true);
        User32.SetForegroundWindow(hwndMain);
        User32.DestroyWindow(_hwndScaleDlg);
        _hwndScaleDlg = IntPtr.Zero;
        _hwndScaleEdit = IntPtr.Zero;
        // hFont는 using 스코프 종료 시 자동 해제 (SafeFontHandle → DeleteObject)

        return result;
    }

    /// <summary>
    /// EDIT 박스 내용을 읽고 파싱. 성공 시 _scaleDlgParsedValue 설정 후 true,
    /// 실패 시 MessageBox로 에러 표시 후 false (대화상자 유지).
    /// </summary>
    private static bool TryCommitScaleInput()
    {
        if (_hwndScaleEdit == IntPtr.Zero) return false;

        char[] buf = new char[32];
        int len = User32.GetWindowTextW(_hwndScaleEdit, buf, buf.Length);
        string text = len > 0 ? new string(buf, 0, len).Trim() : "";

        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
        {
            User32.MessageBoxW(_hwndScaleDlg, I18n.ScaleDialogInvalidInput,
                I18n.ScaleDialogTitle, 0);
            User32.SetFocus(_hwndScaleEdit);
            User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return false;
        }

        if (value < ScaleMinValue - ScaleTolerance || value > ScaleMaxValue + ScaleTolerance)
        {
            User32.MessageBoxW(_hwndScaleDlg, I18n.ScaleDialogOutOfRange,
                I18n.ScaleDialogTitle, 0);
            User32.SetFocus(_hwndScaleEdit);
            User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return false;
        }

        _scaleDlgParsedValue = Math.Round(Math.Clamp(value, ScaleMinValue, ScaleMaxValue), 1);
        return true;
    }

    [UnmanagedCallersOnly]
    private static IntPtr ScaleDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
                // IsDialogMessageW는 Enter → IDOK(1)/DEFPUSHBUTTON ID, Escape → IDCANCEL(2)로
                // 변환해 WM_COMMAND를 쏜다. 우리 OK는 BS_DEFPUSHBUTTON이라 Enter가 IDC_SCALE_OK로
                // 오지만 IDOK도 방어적으로 함께 수락.
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == IDC_SCALE_OK || id == Win32Constants.IDOK)
                {
                    if (TryCommitScaleInput())
                    {
                        _scaleDlgResult = true;
                        _scaleDlgClosed = true;
                    }
                    return IntPtr.Zero;
                }
                if (id == IDC_SCALE_CANCEL || id == Win32Constants.IDCANCEL)
                {
                    _scaleDlgResult = false;
                    _scaleDlgClosed = true;
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;

            case Win32Constants.WM_CLOSE:
                _scaleDlgResult = false;
                _scaleDlgClosed = true;
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
