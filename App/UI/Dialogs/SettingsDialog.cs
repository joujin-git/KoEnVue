using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// 트레이 메뉴 → "상세 설정" 대화상자.
/// 72개 설정 필드를 스크롤 가능한 테이블 형태로 노출한다.
/// DialogShell 이 라이프사이클을 담당하고 본 파일은 자식 컨트롤 생성 + 커밋 + WndProc 만 보유한다.
///
/// <para>
/// 가독성을 위해 partial class 4분할:
/// <list type="bullet">
///   <item><c>SettingsDialog.cs</c> — Show, BuildChildren(+윈도우 생성 헬퍼 5종), TryCommit, WndProc, 레이아웃 상수, 모달 상태</item>
///   <item><c>SettingsDialog.Layout.cs</c> — SettingsLayout 메트릭 struct + BuildLayout 팩토리(DPI 스케일·파생 좌표 순수 산술)</item>
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

    // 라벨 수직 인셋: 같은 행의 입력 컨트롤(top=y, height=rowH)과 시각 정렬을 위해 라벨을
    // y+LabelVInsetPx 에 두고 높이를 rowH-LabelHeightTrimPx 로 줄인다 (텍스트 baseline 보정).
    private const int LabelVInsetPx    = 3;
    private const int LabelHeightTrimPx = 4;

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
            dialogFontFamily: DefaultConfig.DefaultDialogFontFamily,
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

    /// <summary>
    /// 다이얼로그 자식 컨트롤을 생성한다(셸이 창 생성 직후·표시 직전 1회 동기 호출).
    /// 레이아웃 계산은 <see cref="BuildLayout"/> 으로 분리하고, 본 메서드는 그 결과로
    /// 윈도우를 만드는 5단계 헬퍼를 오케스트레이션한다(IMP-2 분해). cross-file static
    /// 부작용(_lineHeight/_viewportClientH/_scrollMax = SettingsDialog.Scroll.cs)을
    /// 본 메서드에 모아 한눈에 보이게 한다 — 헬퍼는 값을 반환하고 대입은 여기서.
    /// </summary>
    private static unsafe void BuildChildren(DialogShellContext ctx, List<RowDef> rows)
    {
        _hwndDialog = ctx.HwndDialog;

        var m = BuildLayout(ctx);
        _lineHeight = m.RowH + m.RowGap;
        _viewportClientH = m.VpH;

        RegisterViewportClass();
        CreateDescriptionLabel(ctx, m);
        _hwndViewport = CreateViewport(ctx, m);

        int totalContentH = LayoutRows(ctx, m, rows);
        _scrollMax = Math.Max(0, totalContentH - _viewportClientH);
        SetupScrollbar(totalContentH);

        CreateButtons(ctx, m);
    }

    /// <summary>뷰포트 윈도우 클래스 등록 (다이얼로그 클래스는 셸이 이미 등록).</summary>
    private static unsafe void RegisterViewportClass()
        => Win32DialogHelper.RegisterStandardClass(
            ViewportClassName,
            (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ViewportProc,
            (IntPtr)(Win32Constants.COLOR_BTNFACE + 1));

    /// <summary>상단 설명 레이블 생성.</summary>
    private static void CreateDescriptionLabel(DialogShellContext ctx, in SettingsLayout m)
    {
        IntPtr hwndDesc = User32.CreateWindowExW(0, "STATIC", I18n.SettingsDialogDescription,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            m.Pad, m.Pad, m.ClientW - m.Pad * 2, m.DescH,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndDesc, ctx.HFont);
    }

    /// <summary>
    /// 스크롤 뷰포트 생성. WS_CLIPCHILDREN + WS_EX_COMPOSITED 로 스크롤 중 플리커 제거(기존 결정 유지).
    /// </summary>
    private static IntPtr CreateViewport(DialogShellContext ctx, in SettingsLayout m)
        => User32.CreateWindowExW(
            Win32Constants.WS_EX_COMPOSITED, ViewportClassName, "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_CLIPCHILDREN
                | Win32Constants.WS_VSCROLL | Win32Constants.WS_BORDER,
            m.VpX, m.VpY, m.VpW, m.VpH,
            ctx.HwndDialog, (IntPtr)IDC_VIEWPORT, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 뷰포트 내부에 섹션 헤더/구분선 + 행(라벨 + 입력 컨트롤)을 위에서 아래로 배치한다.
    /// <c>_scrollChildren</c>(스크롤 추적)과 <c>_fieldInputs</c>(커밋 순회)를 채우는 부작용을 가진다.
    /// 누적 y 를 콘텐츠 총높이(<c>y + ContentPadInner</c>)로 반환 — 스크롤 메트릭 계산의 단일 결합점.
    /// </summary>
    private static unsafe int LayoutRows(DialogShellContext ctx, in SettingsLayout m, List<RowDef> rows)
    {
        int labelX = m.LabelX;
        int controlX = m.ControlX;
        int labelColW = m.LabelColW;
        int controlColW = m.ControlColW;
        int sectionContentW = m.SectionContentW;
        int rowH = m.RowH;
        int rowGap = m.RowGap;
        int comboDropExtra = m.ComboDropExtra;
        int sectionHeadH = m.SectionHeadH;
        int sectionHeadGap = m.SectionHeadGap;
        int sectionTopGap = m.SectionTopGap;
        int sectionSepPostGap = m.SectionSepPostGap;

        int y = m.ContentPadInner;
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
                labelX, y + LabelVInsetPx, labelColW, rowH - LabelHeightTrimPx,
                _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Win32DialogHelper.ApplyFont(hwndLabel, ctx.HFont);
            _scrollChildren.Add((hwndLabel, labelX, y + LabelVInsetPx));

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

        return y + m.ContentPadInner;
    }

    /// <summary>OK/Cancel 버튼을 다이얼로그 하단 중앙에 배치.</summary>
    private static void CreateButtons(DialogShellContext ctx, in SettingsLayout m)
    {
        int btnY = m.ClientH - m.Pad - m.BtnH;
        int btnAreaWidth = m.BtnW * 2 + m.Pad;
        int btnX = (m.ClientW - btnAreaWidth) / 2;

        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogOk,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                | Win32Constants.WS_TABSTOP | Win32Constants.WS_GROUP
                | Win32Constants.BS_DEFPUSHBUTTON,
            btnX, btnY, m.BtnW, m.BtnH,
            ctx.HwndDialog, (IntPtr)IDC_BTN_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, ctx.HFont);

        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogCancel,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + m.BtnW + m.Pad, btnY, m.BtnW, m.BtnH,
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
                    User32.MessageBoxW(_hwndDialog, error ?? "Error", I18n.SettingsDialogTitle, uType: Win32Constants.MB_OK));
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
                int id = (int)(wParam.ToInt64() & Win32Constants.LOWORD_MASK);
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
