using System.Runtime.InteropServices;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// 트레이 메뉴 → "상세 설정" 대화상자.
/// 60개 설정 필드를 스크롤 가능한 테이블 형태로 노출한다.
/// DialogShell 이 라이프사이클을 담당하고 본 파일은 자식 컨트롤 생성 + 커밋 + WndProc 만 보유한다.
///
/// <para>
/// 가독성을 위해 partial class 3분할:
/// <list type="bullet">
///   <item><c>SettingsDialog.cs</c> — Show, BuildChildren, TryCommit, WndProc, 레이아웃 상수, 모달 상태</item>
///   <item><c>SettingsDialog.Fields.cs</c> — FieldDef/RowDef + BuildRowDefs + 팩토리 + 헬퍼</item>
///   <item><c>SettingsDialog.Scroll.cs</c> — 스크롤 상태 + 스크롤 메서드 + ViewportProc</item>
/// </list>
/// </para>
/// </summary>
internal static partial class SettingsDialog
{
    // ================================================================
    // 레이아웃 상수 (96 DPI 기준, ctx.Scale 로 DPI 스케일 적용)
    // ================================================================

    private const int DlgWidth         = 600;
    private const int DlgHeight        = 700;

    private const int DescH            = 20;
    private const int DescGap          = 12;

    private const int ContentPadInner  = 12;
    private const int LabelColW        = 220;
    private const int ColGap           = 14;
    private const int ControlColW      = 260;

    private const int RowH             = 24;
    private const int RowGap           = 6;

    private const int SectionHeadH     = 20;
    private const int SectionSepH      = 2;
    private const int SectionHeadGap   = 6;
    private const int SectionTopGap    = 14;
    private const int SectionSepPostGap = 6;

    private const int BtnW             = 90;
    private const int BtnH             = 30;
    private const int BtnAreaH         = 50;

    private const int ComboDropExtra   = 220;
    private const int ViewportScrollReserve = 24;

    // ================================================================
    // 컨트롤 ID + 윈도우 클래스
    // ================================================================

    private const int IDC_BTN_OK       = 8001;
    private const int IDC_BTN_CANCEL   = 8002;
    private const int IDC_VIEWPORT     = 8003;

    private const string DlgClassName = "KoEnVueSettingsDlg";
    private const string ViewportClassName = "KoEnVueSettingsViewport";

    // ================================================================
    // 모달 상태
    // ================================================================

    private static bool _dlgResult;
    private static bool _dlgClosed;
    private static IntPtr _hwndDialog;
    private static IntPtr _hwndViewport;
    private static IntPtr _hwndMain;
    private static AppConfig _initialConfig = null!;
    private static AppConfig _workingConfig = null!;
    private static Action<AppConfig>? _updateCallback;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 상세 설정 대화상자를 모달로 표시한다.
    /// 확인 → 유효성 통과 시 updateConfig 호출, 실패 시 MessageBox 후 대화상자 유지.
    /// 취소 / 닫기 → 변경 파기.
    /// </summary>
    internal static unsafe void Show(IntPtr hwndMain, AppConfig config, Action<AppConfig> updateConfig)
    {
        _hwndMain = hwndMain;
        _initialConfig = config;
        _workingConfig = config;
        _updateCallback = updateConfig;
        _dlgResult = false;
        _dlgClosed = false;
        _scrollPos = 0;
        _fields.Clear();
        _fieldInputs.Clear();
        _scrollChildren.Clear();

        var rows = BuildRowDefs();

        bool ran = DialogShell.Run(
            hwndOwner: hwndMain,
            className: DlgClassName,
            wndProc: (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&SettingsDlgProc,
            title: I18n.SettingsDialogTitle,
            dlgLogicalWidth: DlgWidth,
            measureDlgHeight: m => m.Scale(DlgHeight),
            useCursorAnchor: false,
            bringToForeground: true,
            buildChildren: ctx => BuildChildren(ctx, rows),
            onAfterShow: null,
            isClosedFlag: ref _dlgClosed);

        if (ran && _dlgResult && _updateCallback != null)
            _updateCallback(_workingConfig);

        _hwndDialog = IntPtr.Zero;
        _hwndViewport = IntPtr.Zero;
        _fields.Clear();
        _fieldInputs.Clear();
        _scrollChildren.Clear();
    }

    // ================================================================
    // 자식 컨트롤 생성 (DialogShell.buildChildren 콜백)
    // ================================================================

    private static unsafe void BuildChildren(DialogShellContext ctx, List<RowDef> rows)
    {
        _hwndDialog = ctx.HwndDialog;

        int pad = ctx.Pad;
        int descH = ctx.Scale(DescH);
        int descGap = ctx.Scale(DescGap);
        int contentPadInner = ctx.Scale(ContentPadInner);
        int labelColW = ctx.Scale(LabelColW);
        int colGap = ctx.Scale(ColGap);
        int controlColW = ctx.Scale(ControlColW);
        int rowH = ctx.Scale(RowH);
        int rowGap = ctx.Scale(RowGap);
        int sectionHeadH = ctx.Scale(SectionHeadH);
        int sectionHeadGap = ctx.Scale(SectionHeadGap);
        int sectionTopGap = ctx.Scale(SectionTopGap);
        int sectionSepPostGap = ctx.Scale(SectionSepPostGap);
        int btnW = ctx.Scale(BtnW);
        int btnH = ctx.Scale(BtnH);
        int btnAreaH = ctx.Scale(BtnAreaH);
        int comboDropExtra = ctx.Scale(ComboDropExtra);
        int viewportScrollReserve = ctx.Scale(ViewportScrollReserve);

        _lineHeight = rowH + rowGap;
        int clientW = ctx.ClientW;
        int clientH = ctx.ClientH;

        // 뷰포트 클래스 등록 (다이얼로그 클래스는 셸이 이미 등록)
        Win32DialogHelper.RegisterStandardClass(
            ViewportClassName,
            (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ViewportProc,
            (IntPtr)(Win32Constants.COLOR_BTNFACE + 1));

        // 설명 레이블
        IntPtr hwndDesc = User32.CreateWindowExW(0, "STATIC", I18n.SettingsDialogDescription,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, pad, clientW - pad * 2, descH,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndDesc, ctx.HFont);

        // 뷰포트
        int vpX = pad;
        int vpY = pad + descH + descGap;
        int vpW = clientW - pad * 2;
        int vpH = clientH - vpY - btnAreaH - pad;
        _viewportClientH = vpH;

        // WS_CLIPCHILDREN + WS_EX_COMPOSITED: 스크롤 중 플리커 제거 (기존 결정 유지)
        _hwndViewport = User32.CreateWindowExW(
            Win32Constants.WS_EX_COMPOSITED, ViewportClassName, "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_CLIPCHILDREN
                | Win32Constants.WS_VSCROLL | Win32Constants.WS_BORDER,
            vpX, vpY, vpW, vpH,
            ctx.HwndDialog, (IntPtr)IDC_VIEWPORT, IntPtr.Zero, IntPtr.Zero);

        // 뷰포트 내부 행 배치
        int labelX = contentPadInner;
        int controlX = contentPadInner + labelColW + colGap;
        int innerContentW = vpW - contentPadInner * 2 - viewportScrollReserve;
        controlColW = Math.Max(1, Math.Min(controlColW, innerContentW - labelColW - colGap));
        int sectionContentW = Math.Max(innerContentW, labelColW + colGap + controlColW);

        int y = contentPadInner;
        bool firstSection = true;
        bool firstControlInSection = false;

        foreach (var rowDef in rows)
        {
            if (rowDef.IsSection)
            {
                if (!firstSection) y += sectionTopGap;
                firstSection = false;
                firstControlInSection = true;

                IntPtr hwndSec = User32.CreateWindowExW(0, "STATIC", rowDef.SectionLabel,
                    Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_GROUP,
                    labelX, y, sectionContentW, sectionHeadH,
                    _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Win32DialogHelper.ApplyFont(hwndSec, ctx.HFont);
                _scrollChildren.Add((hwndSec, labelX, y));
                y += sectionHeadH + sectionHeadGap;

                IntPtr hwndSep = User32.CreateWindowExW(0, "STATIC", "",
                    Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.SS_ETCHEDHORZ,
                    labelX, y, sectionContentW, SectionSepH,
                    _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                _scrollChildren.Add((hwndSep, labelX, y));
                y += SectionSepH + sectionSepPostGap;
                continue;
            }

            var field = rowDef.Field!;

            // 라벨 — 입력 컨트롤 바로 앞 위치 (UIA LabeledBy 자동 연결)
            IntPtr hwndLabel = User32.CreateWindowExW(0, "STATIC", field.Label,
                Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
                labelX, y + 3, labelColW, rowH - 4,
                _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Win32DialogHelper.ApplyFont(hwndLabel, ctx.HFont);
            _scrollChildren.Add((hwndLabel, labelX, y + 3));

            // 첫 컨트롤은 WS_GROUP — 섹션 단위로 화살표 키 그룹 경계
            uint groupStyle = firstControlInSection ? Win32Constants.WS_GROUP : 0u;
            firstControlInSection = false;

            IntPtr hwndInput;
            switch (field.Type)
            {
                case FieldType.Bool:
                {
                    hwndInput = User32.CreateWindowExW(0, "BUTTON", "",
                        Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                            | Win32Constants.BS_AUTOCHECKBOX | Win32Constants.WS_TABSTOP | groupStyle,
                        controlX, y, rowH, rowH,
                        _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    bool initial = field.GetBool!(_workingConfig);
                    User32.SendMessageW(hwndInput, Win32Constants.BM_SETCHECK,
                        (IntPtr)(initial ? Win32Constants.BST_CHECKED : Win32Constants.BST_UNCHECKED),
                        IntPtr.Zero);
                    _scrollChildren.Add((hwndInput, controlX, y));
                    break;
                }
                case FieldType.Combo:
                {
                    hwndInput = User32.CreateWindowExW(0, "COMBOBOX", "",
                        Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP
                            | Win32Constants.CBS_DROPDOWNLIST | Win32Constants.CBS_HASSTRINGS | groupStyle,
                        controlX, y, controlColW, rowH + comboDropExtra,
                        _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    var labels = field.EnumLabels!;
                    foreach (string label in labels)
                    {
                        IntPtr pStr = Marshal.StringToHGlobalUni(label);
                        User32.SendMessageW(hwndInput, Win32Constants.CB_ADDSTRING, IntPtr.Zero, pStr);
                        Marshal.FreeHGlobal(pStr);
                    }
                    int initIdx = field.GetEnumIndex!(_workingConfig);
                    User32.SendMessageW(hwndInput, Win32Constants.CB_SETCURSEL,
                        (IntPtr)initIdx, IntPtr.Zero);
                    _scrollChildren.Add((hwndInput, controlX, y));
                    break;
                }
                default:  // Int / Double / String / Color
                {
                    string initial = field.GetString!(_workingConfig);
                    hwndInput = User32.CreateWindowExW(0, "EDIT", initial,
                        Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_BORDER
                            | Win32Constants.WS_TABSTOP | Win32Constants.ES_LEFT
                            | Win32Constants.ES_AUTOHSCROLL | groupStyle,
                        controlX, y, controlColW, rowH,
                        _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    _scrollChildren.Add((hwndInput, controlX, y));
                    break;
                }
            }
            Win32DialogHelper.ApplyFont(hwndInput, ctx.HFont);
            _fieldInputs.Add(hwndInput);

            y += rowH + rowGap;
        }

        int totalContentH = y + contentPadInner;
        _scrollMax = Math.Max(0, totalContentH - _viewportClientH);
        SetupScrollbar(totalContentH);

        // OK/Cancel
        int btnY = clientH - pad - btnH;
        int btnAreaWidth = btnW * 2 + pad;
        int btnX = (clientW - btnAreaWidth) / 2;

        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogOk,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                | Win32Constants.WS_TABSTOP | Win32Constants.WS_GROUP
                | Win32Constants.BS_DEFPUSHBUTTON,
            btnX, btnY, btnW, btnH,
            ctx.HwndDialog, (IntPtr)IDC_BTN_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, ctx.HFont);

        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogCancel,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + pad, btnY, btnW, btnH,
            ctx.HwndDialog, (IntPtr)IDC_BTN_CANCEL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndCancel, ctx.HFont);
    }

    // ================================================================
    // 커밋 (확인 버튼)
    // ================================================================

    /// <summary>
    /// 모든 필드를 순회하며 유효성 검사를 수행하고, 모두 통과하면 _workingConfig 를 갱신한다.
    /// 실패 시 MessageBox + 문제 컨트롤 포커스 + false 반환 (다이얼로그 유지).
    /// </summary>
    private static bool TryCommit()
    {
        AppConfig newCfg = _initialConfig;
        for (int i = 0; i < _fields.Count; i++)
        {
            var field = _fields[i];
            var hwnd = _fieldInputs[i];
            var (result, error) = field.Commit(newCfg, hwnd, field.Label);
            if (error != null || result == null)
            {
                ModalDialogLoop.RunExternal(_hwndDialog, () =>
                    User32.MessageBoxW(_hwndDialog, error ?? "Error", I18n.SettingsDialogTitle, 0));
                ScrollFieldIntoView(i);
                User32.SetFocus(hwnd);
                if (field.Type is FieldType.Int or FieldType.Double
                    or FieldType.String or FieldType.Color)
                {
                    User32.SendMessageW(hwnd, Win32Constants.EM_SETSEL, IntPtr.Zero, (IntPtr)(-1));
                }
                return false;
            }
            newCfg = result;
        }
        _workingConfig = newCfg;
        return true;
    }

    // ================================================================
    // 다이얼로그 WndProc
    // ================================================================

    [UnmanagedCallersOnly]
    private static IntPtr SettingsDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (DialogShell.HandleStandardCommands(id, IDC_BTN_OK, IDC_BTN_CANCEL,
                    ref _dlgResult, ref _dlgClosed, TryCommit))
                    return IntPtr.Zero;
                return IntPtr.Zero;
            }

            case Win32Constants.WM_MOUSEWHEEL:
                // 휠 이벤트가 대화상자에 왔으면 뷰포트로 포워딩
                if (_hwndViewport != IntPtr.Zero)
                    User32.SendMessageW(_hwndViewport, Win32Constants.WM_MOUSEWHEEL, wParam, lParam);
                return IntPtr.Zero;

            case Win32Constants.WM_CLOSE:
                _dlgResult = false;
                _dlgClosed = true;
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
