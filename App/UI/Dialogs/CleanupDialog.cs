using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// indicator_positions 항목을 체크박스 UI로 선택 삭제하는 Win32 모달 다이얼로그.
/// 항목이 DlgMaxVisibleItems를 초과하면 스크롤 가능한 뷰포트를 표시한다.
/// DialogShell 이 라이프사이클을 담당하고, 본 파일은 자식 컨트롤 생성 + 스크롤 + WndProc 만 보유한다.
/// </summary>
internal static class CleanupDialog
{
    // ================================================================
    // 레이아웃 상수 (96 DPI 기준)
    // ================================================================
    private const int DlgCheckHeight = 22;
    private const int DlgCheckGap = 4;
    private const int DlgButtonWidth = 90;
    private const int DlgButtonHeight = 30;
    private const int DlgMinWidth = 340;
    private const int DlgMaxVisibleItems = 15;
    private const int DlgDescHeight = 20;
    private const int DlgSepGap = 12;
    private const int DlgItemIndent = 20;

    // 컨트롤 ID
    private const int IDC_CHECK_BASE = 5000;
    private const int IDC_SELECT_ALL = 5500;
    private const int IDC_BTN_OK = 5501;
    private const int IDC_BTN_CANCEL = 5502;

    // 윈도우 클래스
    private const string DlgClassName = "KoEnVueCleanupDlg";
    private const string ViewportClassName = "KoEnVueCleanupViewport";

    // ================================================================
    // 모달 상태
    // ================================================================
    private static bool _dlgResult;
    private static bool _dlgClosed;
    private static IntPtr _hwndDialog;
    private static IntPtr _hwndViewport;
    private static readonly List<IntPtr> _checkboxHandles = [];
    private static bool _selectAllState = true;
    // 결과 수집: 모달 루프 종료 후엔 DialogShell 의 finally 가 이미 DestroyWindow 를 호출해
    // _checkboxHandles 의 HWND 가 무효 — BM_GETCHECK 가 silent 실패한다. 따라서 WM_COMMAND
    // IDOK 처리 시점(모달 안, HWND 유효) 에 CommitSelections 가 _items 를 순회해
    // _selectedItems 에 박아두고 Show() 는 _selectedItems 만 반환. SettingsDialog 의
    // _workingConfig / ScaleInputDialog 의 _scaleDlgParsedValue 와 동일 패턴.
    private static List<string> _items = null!;
    private static List<string>? _selectedItems;

    // 스크롤 상태
    private static int _scrollPos;
    private static int _scrollMax;
    private static int _viewportClientH;
    private static int _itemHeight;

    /// <summary>
    /// 체크박스 선택 다이얼로그를 표시하고 선택된 항목을 반환한다. 취소 시 null.
    /// </summary>
    public static unsafe List<string>? Show(IntPtr hwndMain, List<string> items)
    {
        _items = items;
        _selectedItems = null;
        _dlgResult = false;
        _dlgClosed = false;
        _checkboxHandles.Clear();
        _selectAllState = true;
        _scrollPos = 0;
        _scrollMax = 0;

        int visibleCount = Math.Min(items.Count, DlgMaxVisibleItems);
        bool needsScroll = items.Count > DlgMaxVisibleItems;
        string title = I18n.IsKorean ? "위치 기록 정리" : "Clean position history";

        bool ran = DialogShell.Run(
            hwndOwner: hwndMain,
            className: DlgClassName,
            wndProc: (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&CleanupDlgProc,
            title: title,
            dlgLogicalWidth: DlgMinWidth,
            measureDlgHeight: m =>
            {
                int checkH = m.Scale(DlgCheckHeight);
                int checkGap = m.Scale(DlgCheckGap);
                int descH = m.Scale(DlgDescHeight);
                int sepGap = m.Scale(DlgSepGap);
                int btnH = m.Scale(DlgButtonHeight);
                int viewportH = visibleCount * (checkH + checkGap);
                return m.NonClientH + m.Pad
                    + descH + checkGap
                    + checkH + checkGap
                    + sepGap
                    + viewportH
                    + m.Pad + btnH + m.Pad;
            },
            useCursorAnchor: false,
            // bringToForeground: true — 셋 다 통일 (CleanupDialog 가 누락하던 부분).
            // ShowWindow 가 어차피 활성 윈도우라 사용자 체감 변화는 거의 없지만,
            // 트레이 메뉴가 닫힌 직후 다이얼로그가 명시적으로 포어그라운드를 잡도록 한다.
            bringToForeground: true,
            dialogFontFamily: DefaultConfig.DefaultDialogFontFamily,
            buildChildren: ctx => BuildChildren(ctx, items, visibleCount, needsScroll),
            onAfterShow: null,
            isClosedFlag: ref _dlgClosed);

        List<string>? result = (ran && _dlgResult) ? _selectedItems : null;

        _hwndDialog = IntPtr.Zero;
        _hwndViewport = IntPtr.Zero;
        _checkboxHandles.Clear();
        _items = null!;
        _selectedItems = null;
        return result;
    }

    /// <summary>
    /// WM_COMMAND IDOK 처리 시점(모달 안, HWND 유효) 에 호출된다. 모든 체크박스 상태를
    /// 읽어 _selectedItems 정적 필드에 박아둔 뒤 true 반환 — 빈 선택도 정상 처리이며
    /// 호출자가 결과를 보고 후속 동작을 결정한다.
    /// </summary>
    private static bool CommitSelections()
    {
        var selected = new List<string>();
        int count = Math.Min(_items.Count, _checkboxHandles.Count);
        for (int i = 0; i < count; i++)
        {
            IntPtr state = User32.SendMessageW(_checkboxHandles[i],
                Win32Constants.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
            if (state == (IntPtr)Win32Constants.BST_CHECKED)
                selected.Add(_items[i]);
        }
        _selectedItems = selected;
        return true;
    }

    private static unsafe void BuildChildren(
        DialogShellContext ctx, List<string> items, int visibleCount, bool needsScroll)
    {
        _hwndDialog = ctx.HwndDialog;

        int pad = ctx.Pad;
        int checkH = ctx.Scale(DlgCheckHeight);
        int checkGap = ctx.Scale(DlgCheckGap);
        int btnW = ctx.Scale(DlgButtonWidth);
        int btnH = ctx.Scale(DlgButtonHeight);
        int descH = ctx.Scale(DlgDescHeight);
        int sepGap = ctx.Scale(DlgSepGap);
        int itemIndent = ctx.Scale(DlgItemIndent);

        _itemHeight = checkH + checkGap;
        int scrollbarW = needsScroll
            ? User32.GetSystemMetricsForDpi(Win32Constants.SM_CXVSCROLL, ctx.RawDpi) : 0;
        int viewportH = visibleCount * _itemHeight;
        int contentW = ctx.ClientW - pad * 2;

        // 뷰포트 클래스 등록 — 본 다이얼로그가 자체 뷰포트를 소유하므로 셸 외부에서.
        Win32DialogHelper.RegisterStandardClass(
            ViewportClassName,
            (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ViewportProc,
            (IntPtr)(Win32Constants.COLOR_BTNFACE + 1));

        int y = pad;

        // 설명 라벨
        string descText = I18n.IsKorean
            ? "삭제할 위치 기록을 선택하세요."
            : "Select position history to delete.";
        IntPtr hwndDesc = User32.CreateWindowExW(0, "STATIC", descText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, descH,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndDesc, ctx.HFont);
        y += descH + checkGap;

        // "전체 선택" 체크박스 — WS_TABSTOP + WS_GROUP 추가 (a11y baseline)
        string selectAllText = I18n.IsKorean ? "전체 선택" : "Select All";
        IntPtr hwndSelectAll = User32.CreateWindowExW(0, "BUTTON", selectAllText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                | Win32Constants.BS_AUTOCHECKBOX
                | Win32Constants.WS_TABSTOP | Win32Constants.WS_GROUP,
            pad, y, contentW, checkH,
            ctx.HwndDialog, (IntPtr)IDC_SELECT_ALL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndSelectAll, ctx.HFont);
        User32.SendMessageW(hwndSelectAll, Win32Constants.BM_SETCHECK,
            (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
        y += checkH + checkGap;

        // 구분선 (etched horizontal)
        User32.CreateWindowExW(0, "STATIC", "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.SS_ETCHEDHORZ,
            pad, y + sepGap / 2 - 1, contentW, 2,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        y += sepGap;

        // 스크롤 뷰포트
        uint vpStyle = Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_CLIPCHILDREN;
        if (needsScroll) vpStyle |= Win32Constants.WS_VSCROLL;
        _hwndViewport = User32.CreateWindowExW(
            Win32Constants.WS_EX_COMPOSITED, ViewportClassName, "",
            vpStyle, pad, y, contentW, viewportH,
            ctx.HwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _viewportClientH = viewportH;

        // 항목 체크박스 — WS_TABSTOP 추가 (a11y baseline; 첫 항목은 WS_GROUP)
        int checkW = contentW - itemIndent - scrollbarW;
        for (int i = 0; i < items.Count; i++)
        {
            int logicalY = i * _itemHeight;
            uint extraStyle = (i == 0 ? Win32Constants.WS_GROUP : 0u);
            IntPtr hwndCheck = User32.CreateWindowExW(0, "BUTTON", items[i],
                Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                    | Win32Constants.BS_AUTOCHECKBOX
                    | Win32Constants.WS_TABSTOP | extraStyle,
                itemIndent, logicalY, checkW, checkH,
                _hwndViewport, (IntPtr)(IDC_CHECK_BASE + i), IntPtr.Zero, IntPtr.Zero);
            Win32DialogHelper.ApplyFont(hwndCheck, ctx.HFont);
            User32.SendMessageW(hwndCheck, Win32Constants.BM_SETCHECK,
                (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
            _checkboxHandles.Add(hwndCheck);
        }

        // 스크롤바 범위 설정
        if (needsScroll)
        {
            int totalContentH = items.Count * _itemHeight;
            _scrollMax = Math.Max(0, totalContentH - _viewportClientH);
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

        y += viewportH + pad;

        // 버튼
        int btnAreaWidth = btnW * 2 + pad;
        int btnX = (ctx.DlgWidth - btnAreaWidth) / 2;

        string okText = I18n.IsKorean ? "삭제" : "Delete";
        string cancelText = I18n.IsKorean ? "취소" : "Cancel";
        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", okText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE
                | Win32Constants.WS_TABSTOP | Win32Constants.WS_GROUP,
            btnX, y, btnW, btnH,
            ctx.HwndDialog, (IntPtr)IDC_BTN_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, ctx.HFont);
        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", cancelText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + pad, y, btnW, btnH,
            ctx.HwndDialog, (IntPtr)IDC_BTN_CANCEL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndCancel, ctx.HFont);
    }

    // ================================================================
    // 스크롤
    // ================================================================

    private static void ScrollTo(int newPos)
        => ScrollableDialogHelper.ScrollTo(_hwndViewport, ref _scrollPos, _scrollMax, newPos);

    private static int ResolveVScrollPosition(IntPtr hwnd, int scrollCode)
        => ScrollableDialogHelper.ResolveVScrollPosition(
            hwnd, scrollCode, _scrollPos, _scrollMax, _viewportClientH, _itemHeight);

    private static void HandleMouseWheel(IntPtr wParam)
        => ScrollTo(ScrollableDialogHelper.CalculateWheelScrollPos(wParam, _scrollPos, _itemHeight));

    // ================================================================
    // WndProc — 다이얼로그
    // ================================================================

    [UnmanagedCallersOnly]
    private static IntPtr CleanupDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                // CommitSelections 콜백: IDOK 처리 시점(모달 안, HWND 유효) 에 체크박스
                // 상태를 _selectedItems 에 박아둔다. 모달 루프 종료 후 DialogShell 의
                // finally 가 DestroyWindow 를 호출하면 HWND 가 무효가 되므로 늦으면 안 됨.
                if (DialogShell.HandleStandardCommands(id, IDC_BTN_OK, IDC_BTN_CANCEL,
                    ref _dlgResult, ref _dlgClosed, CommitSelections))
                    return IntPtr.Zero;
                if (id == IDC_SELECT_ALL)
                {
                    // 전체 선택/해제 토글
                    _selectAllState = !_selectAllState;
                    IntPtr checkState = (IntPtr)(_selectAllState
                        ? Win32Constants.BST_CHECKED : Win32Constants.BST_UNCHECKED);
                    foreach (IntPtr h in _checkboxHandles)
                        User32.SendMessageW(h, Win32Constants.BM_SETCHECK, checkState, IntPtr.Zero);
                }
                return IntPtr.Zero;
            }

            case Win32Constants.WM_MOUSEWHEEL:
                HandleMouseWheel(wParam);
                return IntPtr.Zero;

            case Win32Constants.WM_CLOSE:
                _dlgResult = false;
                _dlgClosed = true;
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // ================================================================
    // WndProc — 뷰포트
    // ================================================================

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
                HandleMouseWheel(wParam);
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
