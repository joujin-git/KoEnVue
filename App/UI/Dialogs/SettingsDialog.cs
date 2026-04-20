using System.Runtime.InteropServices;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// 트레이 메뉴 → "상세 설정" 대화상자.
/// 트레이 메뉴와 겹치지 않는 60개 설정 필드를 스크롤 가능한 테이블 형태로 노출한다.
/// 룩앤필은 Tray.cs의 CleanupDialog/ScaleDialog와 맞춘다.
///
/// <para>
/// 가독성을 위해 partial class 3분할:
/// <list type="bullet">
///   <item><c>SettingsDialog.cs</c> — Show, WndProc, TryCommit, 레이아웃 상수, 모달 상태</item>
///   <item><c>SettingsDialog.Fields.cs</c> — FieldDef/RowDef + BuildRowDefs + 팩토리 + 헬퍼</item>
///   <item><c>SettingsDialog.Scroll.cs</c> — 스크롤 상태 + 스크롤 메서드 + ViewportProc</item>
/// </list>
/// 모든 분할 파일은 동일한 정적 상태(_fields, _scrollPos 등)를 공유한다.
/// </para>
/// </summary>
internal static partial class SettingsDialog
{
    // ================================================================
    // 레이아웃 상수 (96 DPI 기준, DpiHelper.Scale로 DPI 스케일 적용)
    // ================================================================

    private const int DlgWidth         = 600;
    private const int DlgHeight        = 700;
    private const int DlgPad           = 16;

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

    // COMBOBOX 창 생성 높이는 드롭다운 영역을 포함한다. 표시 영역은 ~ RowH.
    private const int ComboDropExtra   = 220;

    // 뷰포트 스크롤바/보더 여유
    private const int ViewportScrollReserve = 24;

    // ================================================================
    // 컨트롤 ID
    // ================================================================

    private const int IDC_BTN_OK       = 8001;
    private const int IDC_BTN_CANCEL   = 8002;
    private const int IDC_VIEWPORT     = 8003;

    // ================================================================
    // 모달 상태 (단일 모달 대화상자이므로 정적 필드로 충분)
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
    /// 확인 → 유효성 통과 시 updateConfig 호출, 실패 시 MessageBox로 안내하고 대화상자 유지.
    /// 취소 / 닫기 → 변경 파기.
    /// </summary>
    internal static unsafe void Show(IntPtr hwndMain, AppConfig config, Action<AppConfig> updateConfig)
    {
        // 재진입 가드: 이미 다른 모달 다이얼로그가 열려 있으면 그 창으로 포커스만 복원.
        // 트레이 아이콘은 shell32 관리라 EnableWindow(_hwndMain, false) 로 차단되지 않으므로
        // 다이얼로그가 열린 상태에서도 트레이 메뉴 → 같은/다른 다이얼로그 재호출이 가능하다.
        if (ModalDialogLoop.IsActive)
        {
            User32.SetForegroundWindow(ModalDialogLoop.ActiveDialog);
            return;
        }

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

        // DPI 스케일
        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTONEAREST);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);

        int pad = DpiHelper.Scale(DlgPad, dpiScale);
        int descH = DpiHelper.Scale(DescH, dpiScale);
        int descGap = DpiHelper.Scale(DescGap, dpiScale);
        int contentPadInner = DpiHelper.Scale(ContentPadInner, dpiScale);
        int labelColW = DpiHelper.Scale(LabelColW, dpiScale);
        int colGap = DpiHelper.Scale(ColGap, dpiScale);
        int controlColW = DpiHelper.Scale(ControlColW, dpiScale);
        int rowH = DpiHelper.Scale(RowH, dpiScale);
        int rowGap = DpiHelper.Scale(RowGap, dpiScale);
        int sectionHeadH = DpiHelper.Scale(SectionHeadH, dpiScale);
        int sectionHeadGap = DpiHelper.Scale(SectionHeadGap, dpiScale);
        int sectionTopGap = DpiHelper.Scale(SectionTopGap, dpiScale);
        int sectionSepPostGap = DpiHelper.Scale(SectionSepPostGap, dpiScale);
        int btnW = DpiHelper.Scale(BtnW, dpiScale);
        int btnH = DpiHelper.Scale(BtnH, dpiScale);
        int btnAreaH = DpiHelper.Scale(BtnAreaH, dpiScale);
        int dlgWidth = DpiHelper.Scale(DlgWidth, dpiScale);
        int dlgHeight = DpiHelper.Scale(DlgHeight, dpiScale);
        int comboDropExtra = DpiHelper.Scale(ComboDropExtra, dpiScale);
        int viewportScrollReserve = DpiHelper.Scale(ViewportScrollReserve, dpiScale);

        _lineHeight = rowH + rowGap;

        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);
        int nonClientH = Win32DialogHelper.CalculateNonClientHeight(rawDpi);
        int nonClientW = Win32DialogHelper.CalculateNonClientWidth(rawDpi);

        // UI 폰트 (맑은 고딕 9pt, DPI 스케일) — using 스코프 종료 시 자동 DeleteObject
        using var hFont = Win32DialogHelper.CreateDialogFont(dpiY);
        IntPtr hFontRaw = hFont.DangerousGetHandle();

        // 대화상자 클래스 등록 (중복 호출은 무시됨)
        string dlgClassName = "KoEnVueSettingsDlg";
        var dlgWc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&SettingsDlgProc,
            lpszClassName = dlgClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref dlgWc);

        string viewportClassName = "KoEnVueSettingsViewport";
        var vpWc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ViewportProc,
            lpszClassName = viewportClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref vpWc);

        // 모니터 중앙 배치 — 공통 헬퍼 (anchor=null → rcWork 정중앙)
        var (cx, cy) = Win32DialogHelper.CalculateDialogPosition(hMon, dlgWidth, dlgHeight);

        _hwndDialog = User32.CreateWindowExW(0, dlgClassName, I18n.SettingsDialogTitle,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            _hwndMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwndDialog == IntPtr.Zero)
        {
            return;
        }

        // 클라이언트 영역 크기 (타이틀바·보더 제외)
        int clientW = dlgWidth - nonClientW;
        int clientH = dlgHeight - nonClientH;

        // --- 설명 레이블 ---
        IntPtr hwndDesc = User32.CreateWindowExW(0, "STATIC", I18n.SettingsDialogDescription,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, pad, clientW - pad * 2, descH,
            _hwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndDesc, hFontRaw);

        // --- 뷰포트 ---
        int vpX = pad;
        int vpY = pad + descH + descGap;
        int vpW = clientW - pad * 2;
        int vpH = clientH - vpY - btnAreaH - pad;
        _viewportClientH = vpH;

        // WS_CLIPCHILDREN: 부모 페인트·에레이즈에서 자식 영역 제외 (1차 방어).
        // WS_EX_COMPOSITED (dwExStyle): DWM 이 뷰포트와 모든 자식을 오프스크린 비트맵에 합성 후
        // 한 번에 출력하는 더블버퍼링. 120여 개 컨트롤이 동시 이동하는 스크롤 중 중간 상태가
        // 화면에 노출되지 않아 플리커·티어링 제거.
        _hwndViewport = User32.CreateWindowExW(
            Win32Constants.WS_EX_COMPOSITED, viewportClassName, "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_CLIPCHILDREN
                | Win32Constants.WS_VSCROLL | Win32Constants.WS_BORDER,
            vpX, vpY, vpW, vpH,
            _hwndDialog, (IntPtr)IDC_VIEWPORT, IntPtr.Zero, IntPtr.Zero);

        // --- 뷰포트 내부 행 배치 (logical Y 기준) ---
        int labelX = contentPadInner;
        int controlX = contentPadInner + labelColW + colGap;
        int innerContentW = vpW - contentPadInner * 2 - viewportScrollReserve;
        // 입력 컨트롤 너비가 스크롤 예약 영역을 침범하지 않도록 가용 폭으로 보정.
        // 매우 좁은 클라이언트(DPI 고배율 + 작은 다이얼로그)에서 음수가 나오지 않도록 최소 1px 보장.
        controlColW = Math.Max(1, Math.Min(controlColW, innerContentW - labelColW - colGap));
        int sectionContentW = Math.Max(innerContentW, labelColW + colGap + controlColW);

        int y = contentPadInner;
        bool firstSection = true;

        foreach (var rowDef in rows)
        {
            if (rowDef.IsSection)
            {
                if (!firstSection) y += sectionTopGap;
                firstSection = false;

                IntPtr hwndSec = User32.CreateWindowExW(0, "STATIC", rowDef.SectionLabel,
                    Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
                    labelX, y, sectionContentW, sectionHeadH,
                    _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Win32DialogHelper.ApplyFont(hwndSec, hFontRaw);
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

            // 라벨
            IntPtr hwndLabel = User32.CreateWindowExW(0, "STATIC", field.Label,
                Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
                labelX, y + 3, labelColW, rowH - 4,
                _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Win32DialogHelper.ApplyFont(hwndLabel, hFontRaw);
            _scrollChildren.Add((hwndLabel, labelX, y + 3));

            // 컨트롤
            IntPtr hwndInput;
            switch (field.Type)
            {
                case FieldType.Bool:
                {
                    hwndInput = User32.CreateWindowExW(0, "BUTTON", "",
                        Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                            | Win32Constants.BS_AUTOCHECKBOX | Win32Constants.WS_TABSTOP,
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
                            | Win32Constants.CBS_DROPDOWNLIST | Win32Constants.CBS_HASSTRINGS,
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
                            | Win32Constants.WS_TABSTOP | Win32Constants.ES_LEFT | Win32Constants.ES_AUTOHSCROLL,
                        controlX, y, controlColW, rowH,
                        _hwndViewport, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    _scrollChildren.Add((hwndInput, controlX, y));
                    break;
                }
            }
            Win32DialogHelper.ApplyFont(hwndInput, hFontRaw);
            _fieldInputs.Add(hwndInput);

            y += rowH + rowGap;
        }

        int totalContentH = y + contentPadInner;

        // 스크롤바 설정
        _scrollMax = Math.Max(0, totalContentH - _viewportClientH);
        SetupScrollbar(totalContentH);

        // --- OK/Cancel 버튼 ---
        int btnY = clientH - pad - btnH;
        int btnAreaWidth = btnW * 2 + pad;
        int btnX = (clientW - btnAreaWidth) / 2;

        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogOk,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP
                | Win32Constants.BS_DEFPUSHBUTTON,
            btnX, btnY, btnW, btnH,
            _hwndDialog, (IntPtr)IDC_BTN_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, hFontRaw);

        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", I18n.ScaleDialogCancel,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + pad, btnY, btnW, btnH,
            _hwndDialog, (IntPtr)IDC_BTN_CANCEL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndCancel, hFontRaw);

        // 모달 표시 — EnableWindow/메시지 루프/포그라운드 복원은 공용 헬퍼에 위임
        User32.ShowWindow(_hwndDialog, Win32Constants.SW_SHOW);
        User32.SetForegroundWindow(_hwndDialog);

        // 모달 루프 실행 + 정리를 try/finally 로 보호. 루프 중 예외가 전파되어도
        // DestroyWindow 및 정적 상태 초기화가 누수 없이 수행된다.
        try
        {
            ModalDialogLoop.Run(_hwndDialog, _hwndMain, ref _dlgClosed);
        }
        finally
        {
            // 정리
            User32.DestroyWindow(_hwndDialog);
            _hwndDialog = IntPtr.Zero;
            _hwndViewport = IntPtr.Zero;
            _fields.Clear();
            _fieldInputs.Clear();
            _scrollChildren.Clear();
            // hFont는 using 스코프 종료 시 자동 해제 (SafeFontHandle → DeleteObject)
        }

        if (_dlgResult && _updateCallback != null)
            _updateCallback(_workingConfig);
    }

    // ================================================================
    // 커밋 (확인 버튼) — 모든 필드를 순회하며 검증·적용
    // ================================================================

    /// <summary>
    /// 모든 필드를 순회하며 유효성 검사를 수행하고, 모두 통과하면 _workingConfig를 갱신한다.
    /// 실패 시 MessageBox로 에러 표시 + 문제 컨트롤에 포커스 + false 반환.
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
                if (id == IDC_BTN_OK || id == Win32Constants.IDOK)
                {
                    if (TryCommit())
                    {
                        _dlgResult = true;
                        _dlgClosed = true;
                    }
                    return IntPtr.Zero;
                }
                if (id == IDC_BTN_CANCEL || id == Win32Constants.IDCANCEL)
                {
                    _dlgResult = false;
                    _dlgClosed = true;
                    return IntPtr.Zero;
                }
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
