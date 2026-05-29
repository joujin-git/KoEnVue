using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// 인디케이터 배율 직접 입력용 Win32 모달 다이얼로그.
/// 커서 위치 근처에 표시되며 [1.0, 5.0] 범위 외 입력은 에러로 반려한다.
/// DialogShell 이 라이프사이클(reentry guard / DPI / font / class 등록 / 모달 루프 / DestroyWindow)을
/// 담당하고, 본 파일은 자식 컨트롤 생성과 WndProc, 입력 검증만 보유한다.
/// </summary>
internal static class ScaleInputDialog
{
    // ================================================================
    // 범위/허용오차 상수
    // ================================================================
    /// <summary>인디케이터 배율 최솟값 — <see cref="DefaultConfig.MinIndicatorScale"/> 와 동기.</summary>
    public const double ScaleMinValue = DefaultConfig.MinIndicatorScale;
    /// <summary>인디케이터 배율 최댓값 — <see cref="DefaultConfig.MaxIndicatorScale"/> 와 동기.</summary>
    public const double ScaleMaxValue = DefaultConfig.MaxIndicatorScale;
    public const double ScaleTolerance = 0.001;

    // 레이아웃 상수 (96 DPI 기준)
    private const int ScaleDlgWidth = 320;
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

        bool ran = DialogShell.Run(
            hwndOwner: hwndMain,
            className: DlgClassName,
            wndProc: (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ScaleDlgProc,
            title: I18n.ScaleDialogTitle,
            dlgLogicalWidth: ScaleDlgWidth,
            measureDlgHeight: m =>
            {
                int labelH = m.Scale(ScaleDlgLabelH);
                int editH = m.Scale(ScaleDlgEditH);
                int hintH = m.Scale(ScaleDlgHintH);
                int btnH = m.Scale(ScaleDlgBtnH);
                int gap = m.Scale(ScaleDlgGap);
                return m.NonClientH + m.Pad
                    + labelH + gap + editH + gap
                    + hintH + m.Pad + btnH + m.Pad;
            },
            useCursorAnchor: true,
            bringToForeground: true,
            dialogFontFamily: DefaultConfig.DefaultDialogFontFamily,
            buildChildren: ctx => BuildChildren(ctx, initialValue),
            onAfterShow: _ =>
            {
                User32.SetFocus(_hwndScaleEdit);
                User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL,
                    IntPtr.Zero, (IntPtr)(-1));
            },
            isClosedFlag: ref _scaleDlgClosed);

        double? result = (ran && _scaleDlgResult) ? _scaleDlgParsedValue : null;
        _hwndScaleDlg = IntPtr.Zero;
        _hwndScaleEdit = IntPtr.Zero;
        return result;
    }

    private static void BuildChildren(DialogShellContext ctx, double initialValue)
    {
        _hwndScaleDlg = ctx.HwndDialog;

        int pad = ctx.Pad;
        int gap = ctx.Scale(ScaleDlgGap);
        int labelH = ctx.Scale(ScaleDlgLabelH);
        int editH = ctx.Scale(ScaleDlgEditH);
        int hintH = ctx.Scale(ScaleDlgHintH);
        int btnW = ctx.Scale(ScaleDlgBtnW);
        int btnH = ctx.Scale(ScaleDlgBtnH);
        int contentW = ctx.ClientW - pad * 2;

        int y = pad;

        // 안내 레이블 — 입력 컨트롤 바로 앞 (UIA LabeledBy 자동 연결)
        IntPtr hwndLabel = User32.CreateWindowExW(0, "STATIC", I18n.ScaleDialogPrompt,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_GROUP,
            pad, y, contentW, labelH,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndLabel, ctx.HFont);
        y += labelH + gap;

        // EDIT 박스
        string initialText = initialValue.ToString("0.#");
        _hwndScaleEdit = User32.CreateWindowExW(0, "EDIT", initialText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_BORDER
                | Win32Constants.WS_TABSTOP | Win32Constants.ES_LEFT | Win32Constants.ES_AUTOHSCROLL,
            pad, y, contentW, editH,
            ctx.HwndDialog, (IntPtr)IDC_SCALE_EDIT, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(_hwndScaleEdit, ctx.HFont);
        y += editH + gap;

        // 힌트
        IntPtr hwndHint = User32.CreateWindowExW(0, "STATIC", I18n.ScaleDialogHint,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, hintH,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndHint, ctx.HFont);
        y += hintH + pad;

        // 버튼 — 오른쪽 정렬
        int btnAreaWidth = btnW * 2 + gap;
        int btnX = ctx.ClientW - pad - btnAreaWidth;

        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogOk,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP
                | Win32Constants.WS_GROUP | Win32Constants.BS_DEFPUSHBUTTON,
            btnX, y, btnW, btnH,
            ctx.HwndDialog, (IntPtr)IDC_SCALE_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, ctx.HFont);

        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogCancel,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + gap, y, btnW, btnH,
            ctx.HwndDialog, (IntPtr)IDC_SCALE_CANCEL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndCancel, ctx.HFont);
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
            ModalDialogLoop.RunExternal(_hwndScaleDlg, () =>
                User32.MessageBoxW(_hwndScaleDlg, I18n.ScaleDialogInvalidInput,
                    I18n.ScaleDialogTitle, uType: Win32Constants.MB_OK));
            User32.SetFocus(_hwndScaleEdit);
            User32.SendMessageW(_hwndScaleEdit, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
            return false;
        }

        if (value < ScaleMinValue - ScaleTolerance || value > ScaleMaxValue + ScaleTolerance)
        {
            ModalDialogLoop.RunExternal(_hwndScaleDlg, () =>
                User32.MessageBoxW(_hwndScaleDlg, I18n.ScaleDialogOutOfRange,
                    I18n.ScaleDialogTitle, uType: Win32Constants.MB_OK));
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
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (DialogShell.HandleStandardCommands(id, IDC_SCALE_OK, IDC_SCALE_CANCEL,
                    ref _scaleDlgResult, ref _scaleDlgClosed, TryCommitScaleInput))
                    return IntPtr.Zero;
                return IntPtr.Zero;
            }

            case Win32Constants.WM_CLOSE:
                _scaleDlgResult = false;
                _scaleDlgClosed = true;
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
