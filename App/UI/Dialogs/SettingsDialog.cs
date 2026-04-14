using System.Globalization;
using System.Runtime.InteropServices;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// 트레이 메뉴 → "상세 설정" 대화상자.
/// 트레이 메뉴와 겹치지 않는 59개 설정 필드를 스크롤 가능한 테이블 형태로 노출한다.
/// 룩앤필은 Tray.cs의 CleanupDialog/ScaleDialog와 맞춘다.
/// </summary>
internal static class SettingsDialog
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

    // 마우스 휠 한 틱당 스크롤 라인 수 (한 라인 = RowH + RowGap)
    private const int WheelLineStep    = 3;

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

    // 스크롤 상태
    private static int _scrollPos;
    private static int _scrollMax;
    private static int _viewportClientH;
    private static int _lineHeight;

    // 필드/컨트롤 추적
    private static readonly List<FieldDef> _fields = new();
    private static readonly List<IntPtr> _fieldInputs = new();  // _fields 와 같은 순서·같은 길이
    private static readonly List<(IntPtr Hwnd, int X, int LogicalY)> _scrollChildren = new();

    // ================================================================
    // 필드 메타데이터
    // ================================================================

    private enum FieldType { Bool, Int, Double, String, Color, Combo }

    private sealed class FieldDef
    {
        public required FieldType Type { get; init; }
        public required string LabelKo { get; init; }
        public required string LabelEn { get; init; }
        public string Label => I18n.IsKorean ? LabelKo : LabelEn;

        public Func<AppConfig, bool>? GetBool { get; init; }
        public Func<AppConfig, string>? GetString { get; init; }
        public Func<AppConfig, int>? GetEnumIndex { get; init; }

        public required Func<AppConfig, IntPtr, string, (AppConfig? Config, string? Error)> Commit { get; init; }

        public string[]? EnumLabels { get; init; }
    }

    private sealed class RowDef
    {
        public bool IsSection { get; init; }
        public string? SectionKo { get; init; }
        public string? SectionEn { get; init; }
        public FieldDef? Field { get; init; }
        public string SectionLabel => I18n.IsKorean ? (SectionKo ?? "") : (SectionEn ?? "");
    }

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

        _hwndViewport = User32.CreateWindowExW(
            0, viewportClassName, "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                | Win32Constants.WS_VSCROLL | Win32Constants.WS_BORDER,
            vpX, vpY, vpW, vpH,
            _hwndDialog, (IntPtr)IDC_VIEWPORT, IntPtr.Zero, IntPtr.Zero);

        // --- 뷰포트 내부 행 배치 (logical Y 기준) ---
        int labelX = contentPadInner;
        int controlX = contentPadInner + labelColW + colGap;
        int innerContentW = vpW - contentPadInner * 2 - viewportScrollReserve;
        // 입력 컨트롤 너비가 스크롤 예약 영역을 침범하지 않도록 가용 폭으로 보정
        controlColW = Math.Min(controlColW, innerContentW - labelColW - colGap);
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

        ModalDialogLoop.Run(_hwndDialog, _hwndMain, ref _dlgClosed);

        // 정리
        User32.DestroyWindow(_hwndDialog);
        _hwndDialog = IntPtr.Zero;
        _hwndViewport = IntPtr.Zero;
        _fields.Clear();
        _fieldInputs.Clear();
        _scrollChildren.Clear();
        // hFont는 using 스코프 종료 시 자동 해제 (SafeFontHandle → DeleteObject)

        if (_dlgResult && _updateCallback != null)
            _updateCallback(_workingConfig);
    }

    // ================================================================
    // 스크롤 지원
    // ================================================================

    private static void SetupScrollbar(int totalContentH)
    {
        var si = new SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = Win32Constants.SIF_RANGE | Win32Constants.SIF_PAGE | Win32Constants.SIF_POS,
            nMin = 0,
            nMax = Math.Max(0, totalContentH - 1),
            nPage = (uint)Math.Max(1, _viewportClientH),
            nPos = 0,
        };
        User32.SetScrollInfo(_hwndViewport, Win32Constants.SB_VERT, ref si, true);
    }

    /// <summary>
    /// 스크롤 위치를 newPos로 이동하고 모든 자식 컨트롤을 재배치.
    /// logicalY - newPos 로 실제 Y 좌표를 계산한다.
    /// </summary>
    private static void ScrollTo(int newPos)
    {
        newPos = Math.Clamp(newPos, 0, _scrollMax);
        if (newPos == _scrollPos) return;

        _scrollPos = newPos;

        var si = new SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = Win32Constants.SIF_POS,
            nPos = newPos,
        };
        User32.SetScrollInfo(_hwndViewport, Win32Constants.SB_VERT, ref si, true);

        foreach (var (h, x, logicalY) in _scrollChildren)
        {
            User32.SetWindowPos(h, IntPtr.Zero, x, logicalY - newPos, 0, 0,
                Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER);
        }

        User32.InvalidateRect(_hwndViewport, IntPtr.Zero, true);
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
                User32.MessageBoxW(_hwndDialog, error ?? "Error", I18n.SettingsDialogTitle, 0);
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

    /// <summary>필드 i가 화면에 보이도록 스크롤 위치를 조정.</summary>
    private static void ScrollFieldIntoView(int fieldIndex)
    {
        if (fieldIndex < 0 || fieldIndex >= _fieldInputs.Count) return;
        IntPtr hwnd = _fieldInputs[fieldIndex];
        int logicalY = -1;
        foreach (var (h, _, ly) in _scrollChildren)
        {
            if (h == hwnd) { logicalY = ly; break; }
        }
        if (logicalY < 0) return;

        int visibleTop = _scrollPos;
        int visibleBottom = _scrollPos + _viewportClientH;
        if (logicalY < visibleTop)
            ScrollTo(logicalY);
        else if (logicalY + _lineHeight > visibleBottom)
            ScrollTo(logicalY - _viewportClientH + _lineHeight * 2);
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

    [UnmanagedCallersOnly]
    private static IntPtr ViewportProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_VSCROLL:
            {
                int scrollCode = (int)(wParam.ToInt64() & 0xFFFF);
                ScrollTo(ResolveVScrollPosition(hwnd, scrollCode));
                return IntPtr.Zero;
            }

            case Win32Constants.WM_MOUSEWHEEL:
            {
                short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                int steps = delta / Win32Constants.WHEEL_DELTA;
                int newPos = _scrollPos - steps * WheelLineStep * _lineHeight;
                ScrollTo(newPos);
                return IntPtr.Zero;
            }

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>
    /// SB_* 스크롤 코드를 목표 스크롤 위치로 해석. WM_VSCROLL 핸들러에서 분리하여 가독성 향상.
    /// 알 수 없는 코드는 현재 위치를 그대로 반환해 ScrollTo 가 no-op 이 되도록 한다.
    /// </summary>
    private static int ResolveVScrollPosition(IntPtr hwnd, int scrollCode)
    {
        int lineStep = _lineHeight;
        int pageStep = _viewportClientH > lineStep ? _viewportClientH - lineStep : lineStep * 5;

        switch (scrollCode)
        {
            case Win32Constants.SB_LINEUP: return _scrollPos - lineStep;
            case Win32Constants.SB_LINEDOWN: return _scrollPos + lineStep;
            case Win32Constants.SB_PAGEUP: return _scrollPos - pageStep;
            case Win32Constants.SB_PAGEDOWN: return _scrollPos + pageStep;
            case Win32Constants.SB_TOP: return 0;
            case Win32Constants.SB_BOTTOM: return _scrollMax;
            case Win32Constants.SB_THUMBPOSITION:
            case Win32Constants.SB_THUMBTRACK:
            {
                var si = new SCROLLINFO
                {
                    cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
                    fMask = Win32Constants.SIF_TRACKPOS,
                };
                return User32.GetScrollInfo(hwnd, Win32Constants.SB_VERT, ref si)
                    ? si.nTrackPos
                    : _scrollPos;
            }
            default: return _scrollPos;
        }
    }

    // ================================================================
    // 필드 정의 빌더
    // ================================================================

    /// <summary>
    /// 13개 섹션 × 59개 필드의 RowDef 리스트를 빌드하며 _fields를 채운다.
    /// 각 섹션 제목, 라벨, 검증 범위는 언어(I18n.IsKorean)에 따라 결정된다.
    /// </summary>
    private static List<RowDef> BuildRowDefs()
    {
        _fields.Clear();
        var rows = new List<RowDef>();
        bool ko = I18n.IsKorean;

        void Sec(string sko, string sen)
            => rows.Add(new RowDef { IsSection = true, SectionKo = sko, SectionEn = sen });

        void Add(FieldDef f)
        {
            _fields.Add(f);
            rows.Add(new RowDef { IsSection = false, Field = f });
        }

        // ================================================================
        // 1. 표시 모드
        // ================================================================
        Sec("표시 모드", "Display Mode");
        Add(Combo("표시 방식", "Display mode",
            ko ? ["이벤트 시", "항상"] : ["On Event", "Always"],
            c => (int)c.DisplayMode,
            (c, i) => c with { DisplayMode = (DisplayMode)Math.Clamp(i, 0, 1) }));
        Add(Int("이벤트 표시 시간 (ms)", "Event display duration (ms)", 500, 10000,
            c => c.EventDisplayDurationMs,
            (c, v) => c with { EventDisplayDurationMs = v }));
        Add(Int("항상 모드 유휴 전환 (ms)", "Always-mode idle timeout (ms)", 1000, 30000,
            c => c.AlwaysIdleTimeoutMs,
            (c, v) => c with { AlwaysIdleTimeoutMs = v }));
        Add(Bool("포커스 변경 시 이벤트", "Trigger on focus change",
            c => c.EventTriggers.OnFocusChange,
            (c, v) => c with { EventTriggers = c.EventTriggers with { OnFocusChange = v } }));
        Add(Bool("IME 변경 시 이벤트", "Trigger on IME change",
            c => c.EventTriggers.OnImeChange,
            (c, v) => c with { EventTriggers = c.EventTriggers with { OnImeChange = v } }));

        // ================================================================
        // 2. 외관 — 크기·테두리
        // ================================================================
        Sec("외관 — 크기·테두리", "Appearance — Size & Border");
        Add(Int("라벨 너비 (px)", "Label width (px)", 16, 128,
            c => c.LabelWidth, (c, v) => c with { LabelWidth = v }));
        Add(Int("라벨 높이 (px)", "Label height (px)", 12, 96,
            c => c.LabelHeight, (c, v) => c with { LabelHeight = v }));
        Add(Int("테두리 둥글기 (px)", "Border radius (px)", 0, 48,
            c => c.LabelBorderRadius, (c, v) => c with { LabelBorderRadius = v }));
        Add(Int("테두리 두께 (px)", "Border width (px)", 0, 8,
            c => c.BorderWidth, (c, v) => c with { BorderWidth = v }));
        Add(ColorField("테두리 색상", "Border color",
            c => c.BorderColor, (c, v) => c with { BorderColor = v }));

        // ================================================================
        // 3. 외관 — 색상·투명도
        // ================================================================
        Sec("외관 — 색상·투명도", "Appearance — Colors & Opacity");
        Add(ColorField("한글 배경색", "Hangul background",
            c => c.HangulBg, (c, v) => c with { HangulBg = v }));
        Add(ColorField("한글 글자색", "Hangul foreground",
            c => c.HangulFg, (c, v) => c with { HangulFg = v }));
        Add(ColorField("영문 배경색", "English background",
            c => c.EnglishBg, (c, v) => c with { EnglishBg = v }));
        Add(ColorField("영문 글자색", "English foreground",
            c => c.EnglishFg, (c, v) => c with { EnglishFg = v }));
        Add(ColorField("비한국어 배경색", "Non-Korean background",
            c => c.NonKoreanBg, (c, v) => c with { NonKoreanBg = v }));
        Add(ColorField("비한국어 글자색", "Non-Korean foreground",
            c => c.NonKoreanFg, (c, v) => c with { NonKoreanFg = v }));
        Add(Dbl("유휴 투명도", "Idle opacity", 0.1, 1.0,
            c => c.IdleOpacity, (c, v) => c with { IdleOpacity = v }));
        Add(Dbl("활성 투명도", "Active opacity", 0.1, 1.0,
            c => c.ActiveOpacity, (c, v) => c with { ActiveOpacity = v }));

        // ================================================================
        // 4. 외관 — 텍스트
        // ================================================================
        Sec("외관 — 텍스트", "Appearance — Text");
        Add(Str("글꼴", "Font family",
            c => c.FontFamily, (c, v) => c with { FontFamily = v }, allowEmpty: false));
        Add(Int("글꼴 크기", "Font size", 8, 36,
            c => c.FontSize, (c, v) => c with { FontSize = v }));
        Add(Combo("글꼴 굵기", "Font weight",
            ko ? ["보통", "굵게"] : ["Normal", "Bold"],
            c => (int)c.FontWeight,
            (c, i) => c with { FontWeight = (FontWeight)Math.Clamp(i, 0, 1) }));
        Add(Str("한글 라벨", "Hangul label",
            c => c.HangulLabel, (c, v) => c with { HangulLabel = v }, allowEmpty: false));
        Add(Str("영문 라벨", "English label",
            c => c.EnglishLabel, (c, v) => c with { EnglishLabel = v }, allowEmpty: false));
        Add(Str("비한국어 라벨", "Non-Korean label",
            c => c.NonKoreanLabel, (c, v) => c with { NonKoreanLabel = v }, allowEmpty: false));

        // ================================================================
        // 5. 외관 — 테마
        // ================================================================
        Sec("외관 — 테마", "Appearance — Theme");
        Add(Combo("테마", "Theme",
            ko ? ["사용자 지정", "미니멀", "비비드", "파스텔", "다크", "시스템"]
               : ["Custom", "Minimal", "Vivid", "Pastel", "Dark", "System"],
            c => (int)c.Theme,
            (c, i) => c with { Theme = (Theme)Math.Clamp(i, 0, 5) }));

        // ================================================================
        // 6. 애니메이션
        // ================================================================
        // AnimationEnabled / ChangeHighlight는 트레이 메뉴에서 토글 가능하므로 여기서 제외.
        Sec("애니메이션", "Animation");
        Add(Int("페이드 인 (ms)", "Fade in (ms)", 0, 2000,
            c => c.FadeInMs, (c, v) => c with { FadeInMs = v }));
        Add(Int("페이드 아웃 (ms)", "Fade out (ms)", 0, 2000,
            c => c.FadeOutMs, (c, v) => c with { FadeOutMs = v }));
        Add(Dbl("강조 배율", "Highlight scale", 1.0, 2.0,
            c => c.HighlightScale, (c, v) => c with { HighlightScale = v }));
        Add(Int("강조 지속 시간 (ms)", "Highlight duration (ms)", 0, 2000,
            c => c.HighlightDurationMs, (c, v) => c with { HighlightDurationMs = v }));
        Add(Bool("슬라이드 애니메이션", "Slide animation",
            c => c.SlideAnimation, (c, v) => c with { SlideAnimation = v }));
        Add(Int("슬라이드 속도 (ms)", "Slide speed (ms)", 0, 2000,
            c => c.SlideSpeedMs, (c, v) => c with { SlideSpeedMs = v }));

        // ================================================================
        // 7. 감지 및 숨김
        // ================================================================
        Sec("감지 및 숨김", "Detection & Hiding");
        Add(Int("감지 주기 (ms)", "Poll interval (ms)", 50, 500,
            c => c.PollIntervalMs, (c, v) => c with { PollIntervalMs = v }));
        Add(Combo("감지 방식", "Detection method",
            ko ? ["자동", "IME 기본 윈도우", "IME 컨텍스트", "키보드 레이아웃"]
               : ["Auto", "IME default", "IME context", "Keyboard layout"],
            c => (int)c.DetectionMethod,
            (c, i) => c with { DetectionMethod = (DetectionMethod)Math.Clamp(i, 0, 3) }));
        Add(Combo("비한국어 IME 처리", "Non-Korean IME mode",
            ko ? ["숨김", "표시", "어둡게"] : ["Hide", "Show", "Dim"],
            c => (int)c.NonKoreanIme,
            (c, i) => c with { NonKoreanIme = (NonKoreanImeMode)Math.Clamp(i, 0, 2) }));
        Add(Bool("전체화면에서 숨기기", "Hide in fullscreen",
            c => c.HideInFullscreen, (c, v) => c with { HideInFullscreen = v }));
        Add(Bool("포커스 없을 때 숨기기", "Hide when no focus",
            c => c.HideWhenNoFocus, (c, v) => c with { HideWhenNoFocus = v }));
        Add(Bool("잠금 화면에서 숨기기", "Hide on lock screen",
            c => c.HideOnLockScreen, (c, v) => c with { HideOnLockScreen = v }));

        // ================================================================
        // 8. 앱별 프로필
        // ================================================================
        Sec("앱별 프로필", "App Profiles");
        Add(Combo("매칭 기준", "Match by",
            ko ? ["프로세스", "윈도우 클래스", "윈도우 타이틀"]
               : ["Process", "Window class", "Window title"],
            c => (int)c.AppProfileMatch,
            (c, i) => c with { AppProfileMatch = (AppProfileMatch)Math.Clamp(i, 0, 2) }));
        Add(Combo("필터 모드", "Filter mode",
            ko ? ["블랙리스트 (목록 숨김)", "화이트리스트 (목록만 표시)"]
               : ["Blacklist (hide listed)", "Whitelist (show only listed)"],
            c => (int)c.AppFilterMode,
            (c, i) => c with { AppFilterMode = (AppFilterMode)Math.Clamp(i, 0, 1) }));

        // ================================================================
        // 9. 핫키
        // ================================================================
        Sec("핫키", "Hotkeys");
        Add(Bool("핫키 사용", "Hotkeys enabled",
            c => c.HotkeysEnabled, (c, v) => c with { HotkeysEnabled = v }));
        Add(Str("표시 토글 핫키", "Toggle visibility hotkey",
            c => c.HotkeyToggleVisibility, (c, v) => c with { HotkeyToggleVisibility = v },
            allowEmpty: true));

        // ================================================================
        // 10. 트레이
        // ================================================================
        Sec("트레이", "Tray");
        Add(Combo("아이콘 스타일", "Icon style",
            ko ? ["캐럿+점", "고정"] : ["Caret+dot", "Static"],
            c => (int)c.TrayIconStyle,
            (c, i) => c with { TrayIconStyle = (TrayIconStyle)Math.Clamp(i, 0, 1) }));
        Add(Bool("툴팁 표시", "Show tooltip",
            c => c.TrayTooltip, (c, v) => c with { TrayTooltip = v }));
        Add(Combo("좌클릭 동작", "Left-click action",
            ko ? ["표시 토글", "설정 파일 열기", "동작 없음"]
               : ["Toggle visibility", "Open settings", "None"],
            c => (int)c.TrayClickAction,
            (c, i) => c with { TrayClickAction = (TrayClickAction)Math.Clamp(i, 0, 2) }));
        Add(Bool("상태 변경 알림", "Show state-change notification",
            c => c.TrayShowNotification, (c, v) => c with { TrayShowNotification = v }));
        Add(Dbl("빠른 투명도 1 (진하게)", "Quick opacity 1 (High)", 0.1, 1.0,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 0, 0.95),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 0, v) }));
        Add(Dbl("빠른 투명도 2 (보통)", "Quick opacity 2 (Normal)", 0.1, 1.0,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 1, 0.85),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 1, v) }));
        Add(Dbl("빠른 투명도 3 (연하게)", "Quick opacity 3 (Low)", 0.1, 1.0,
            c => GetPresetAt(c.TrayQuickOpacityPresets, 2, 0.6),
            (c, v) => c with { TrayQuickOpacityPresets = SetPresetAt(c.TrayQuickOpacityPresets, 2, v) }));

        // ================================================================
        // 11. 시스템
        // ================================================================
        Sec("시스템", "System");
        Add(Bool("최소화 상태로 시작", "Start minimized",
            c => c.StartupMinimized, (c, v) => c with { StartupMinimized = v }));
        Add(Bool("단일 인스턴스", "Single instance",
            c => c.SingleInstance, (c, v) => c with { SingleInstance = v }));
        Add(Combo("언어", "Language",
            ko ? ["자동", "한국어", "English"] : ["Auto", "Korean", "English"],
            c => LanguageToIndex(c.Language),
            (c, i) => c with { Language = IndexToLanguage(i) }));
        Add(Combo("로그 레벨", "Log level",
            ko ? ["디버그", "정보", "경고", "오류"] : ["Debug", "Info", "Warning", "Error"],
            c => (int)c.LogLevel,
            (c, i) => c with { LogLevel = (LogLevel)Math.Clamp(i, 0, 3) }));
        Add(Bool("파일에 로그 기록", "Log to file",
            c => c.LogToFile, (c, v) => c with { LogToFile = v }));
        Add(Str("로그 파일 경로", "Log file path",
            c => c.LogFilePath, (c, v) => c with { LogFilePath = v }, allowEmpty: true));
        Add(Int("로그 최대 크기 (MB)", "Log max size (MB)", 1, 100,
            c => c.LogMaxSizeMb, (c, v) => c with { LogMaxSizeMb = v }));

        // ================================================================
        // 12. 다중 모니터
        // ================================================================
        Sec("다중 모니터", "Multi-Monitor");
        Add(Bool("모니터별 DPI 스케일", "Per-monitor DPI scaling",
            c => c.PerMonitorScale, (c, v) => c with { PerMonitorScale = v }));
        Add(Bool("작업 영역 안으로 제한", "Clamp to work area",
            c => c.ClampToWorkArea, (c, v) => c with { ClampToWorkArea = v }));

        // ================================================================
        // 13. 고급
        // ================================================================
        Sec("고급", "Advanced");
        Add(Int("TOPMOST 강제 주기 (ms)", "Force topmost interval (ms)", 0, 60000,
            c => c.Advanced.ForceTopmostIntervalMs,
            (c, v) => c with { Advanced = c.Advanced with { ForceTopmostIntervalMs = v } }));
        Add(Bool("절전 방지", "Prevent sleep",
            c => c.Advanced.PreventSleep,
            (c, v) => c with { Advanced = c.Advanced with { PreventSleep = v } }));

        return rows;
    }

    // ================================================================
    // FieldDef 팩토리
    // ================================================================

    private static FieldDef Bool(string ko, string en,
        Func<AppConfig, bool> get, Func<AppConfig, bool, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Bool,
            LabelKo = ko,
            LabelEn = en,
            GetBool = get,
            Commit = (cfg, hwnd, _) =>
            {
                IntPtr state = User32.SendMessageW(hwnd, Win32Constants.BM_GETCHECK,
                    IntPtr.Zero, IntPtr.Zero);
                bool v = state == (IntPtr)Win32Constants.BST_CHECKED;
                return (set(cfg, v), null);
            },
        };
    }

    private static FieldDef Int(string ko, string en, int min, int max,
        Func<AppConfig, int> get, Func<AppConfig, int, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Int,
            LabelKo = ko,
            LabelEn = en,
            GetString = cfg => get(cfg).ToString(CultureInfo.InvariantCulture),
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidNumberFmt, label));
                if (v < min || v > max)
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsOutOfRangeFmt, label, min, max));
                return (set(cfg, v), null);
            },
        };
    }

    private static FieldDef Dbl(string ko, string en, double min, double max,
        Func<AppConfig, double> get, Func<AppConfig, double, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Double,
            LabelKo = ko,
            LabelEn = en,
            GetString = cfg => get(cfg).ToString("0.###", CultureInfo.InvariantCulture),
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidNumberFmt, label));
                if (v < min || v > max)
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsOutOfRangeFmt, label,
                        min.ToString("0.##", CultureInfo.InvariantCulture),
                        max.ToString("0.##", CultureInfo.InvariantCulture)));
                return (set(cfg, v), null);
            },
        };
    }

    private static FieldDef Str(string ko, string en,
        Func<AppConfig, string> get, Func<AppConfig, string, AppConfig> set, bool allowEmpty)
    {
        return new FieldDef
        {
            Type = FieldType.String,
            LabelKo = ko,
            LabelEn = en,
            GetString = get,
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!allowEmpty && string.IsNullOrWhiteSpace(text))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsEmptyNotAllowedFmt, label));
                return (set(cfg, text), null);
            },
        };
    }

    private static FieldDef ColorField(string ko, string en,
        Func<AppConfig, string> get, Func<AppConfig, string, AppConfig> set)
    {
        return new FieldDef
        {
            Type = FieldType.Color,
            LabelKo = ko,
            LabelEn = en,
            GetString = get,
            Commit = (cfg, hwnd, label) =>
            {
                string text = ReadEdit(hwnd);
                if (!ColorHelper.TryNormalizeHex(text, out string normalized))
                    return (null, string.Format(CultureInfo.InvariantCulture,
                        I18n.SettingsInvalidColorFmt, label));
                return (set(cfg, normalized), null);
            },
        };
    }

    private static FieldDef Combo(string ko, string en, string[] labels,
        Func<AppConfig, int> getIdx, Func<AppConfig, int, AppConfig> setIdx)
    {
        return new FieldDef
        {
            Type = FieldType.Combo,
            LabelKo = ko,
            LabelEn = en,
            EnumLabels = labels,
            GetEnumIndex = getIdx,
            Commit = (cfg, hwnd, _) =>
            {
                IntPtr sel = User32.SendMessageW(hwnd, Win32Constants.CB_GETCURSEL,
                    IntPtr.Zero, IntPtr.Zero);
                int idx = (int)sel.ToInt64();
                if (idx < 0) idx = 0;
                if (idx >= labels.Length) idx = labels.Length - 1;
                return (setIdx(cfg, idx), null);
            },
        };
    }

    // ================================================================
    // 헬퍼
    // ================================================================

    private static string ReadEdit(IntPtr hwnd)
    {
        int len = User32.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        char[] buf = new char[len + 2];
        int read = User32.GetWindowTextW(hwnd, buf, buf.Length);
        return read > 0 ? new string(buf, 0, read).Trim() : "";
    }

    /// <summary>
    /// TrayQuickOpacityPresets 배열의 i번째 값을 안전하게 읽는다 (배열 길이 부족 시 fallback).
    /// </summary>
    private static double GetPresetAt(double[] presets, int i, double fallback)
        => presets != null && i >= 0 && i < presets.Length ? presets[i] : fallback;

    /// <summary>
    /// TrayQuickOpacityPresets 배열의 i번째 값을 갱신한 새 배열을 반환한다.
    /// 길이가 부족하면 3개로 확장하고 기본값으로 채운다.
    /// </summary>
    private static double[] SetPresetAt(double[] original, int i, double newValue)
    {
        double[] source = original ?? [0.95, 0.85, 0.6];
        int len = Math.Max(source.Length, 3);
        var copy = new double[len];
        Array.Copy(source, copy, source.Length);
        if (source.Length < len)
        {
            double[] defaults = [0.95, 0.85, 0.6];
            for (int k = source.Length; k < len && k < defaults.Length; k++)
                copy[k] = defaults[k];
        }
        if (i >= 0 && i < copy.Length) copy[i] = newValue;
        return copy;
    }

    /// <summary>"ko"/"en"/"auto" 문자열을 콤보박스 인덱스(0=auto, 1=ko, 2=en)로 매핑.</summary>
    private static int LanguageToIndex(string lang) => lang switch
    {
        "ko" => 1,
        "en" => 2,
        _    => 0,
    };

    /// <summary>콤보박스 인덱스(0=auto, 1=ko, 2=en)를 설정 문자열로 매핑.</summary>
    private static string IndexToLanguage(int i) => i switch
    {
        1 => "ko",
        2 => "en",
        _ => "auto",
    };
}
