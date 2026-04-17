using System.Runtime.InteropServices;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// indicator_positions 항목을 체크박스 UI로 선택 삭제하는 Win32 모달 다이얼로그.
/// 항목이 DlgMaxVisibleItems를 초과하면 스크롤 가능한 뷰포트를 표시한다.
/// </summary>
internal static class CleanupDialog
{
    // ================================================================
    // 레이아웃 상수 (96 DPI 기준, DPI 스케일 적용)
    // ================================================================
    private const int DlgPadding = 16;
    private const int DlgCheckHeight = 22;
    private const int DlgCheckGap = 4;
    private const int DlgButtonWidth = 90;
    private const int DlgButtonHeight = 30;
    private const int DlgMinWidth = 340;
    private const int DlgMaxVisibleItems = 15;
    private const int DlgDescHeight = 20;
    private const int DlgSepGap = 12;
    private const int DlgItemIndent = 20;

    // 스크롤 상수
    private const int WheelLineStep = 3;

    // 다이얼로그 컨트롤 ID
    private const int IDC_CHECK_BASE = 5000;
    private const int IDC_SELECT_ALL = 5500;
    private const int IDC_BTN_OK = 5501;
    private const int IDC_BTN_CANCEL = 5502;

    // Win32 윈도우 클래스 이름
    private const string DlgClassName = "KoEnVueCleanupDlg";
    private const string ViewportClassName = "KoEnVueCleanupViewport";

    // ================================================================
    // 다이얼로그 상태 (모달 루프용)
    // ================================================================
    private static bool _dlgResult;
    private static bool _dlgClosed;
    private static IntPtr _hwndDialog;
    private static IntPtr _hwndViewport;
    private static readonly List<IntPtr> _checkboxHandles = [];
    private static bool _selectAllState = true;

    // ================================================================
    // 스크롤 상태
    // ================================================================
    private static int _scrollPos;
    private static int _scrollMax;
    private static int _viewportClientH;
    private static int _itemHeight;  // DPI-scaled (checkH + checkGap)
    private static int _checkItemX;  // DPI-scaled itemIndent (뷰포트 내 체크박스 X 좌표)
    private static readonly List<(IntPtr Hwnd, int LogicalY)> _scrollChildren = [];

    /// <summary>
    /// 체크박스 선택 다이얼로그를 표시하고 선택된 항목을 반환한다. 취소 시 null.
    /// </summary>
    public static unsafe List<string>? Show(IntPtr hwndMain, List<string> items)
    {
        // 재진입 가드: 이미 다른 모달 다이얼로그가 열려 있으면 그 창으로 포커스만 복원.
        // 트레이 아이콘은 shell32 관리라 EnableWindow(_hwndMain, false) 로 차단되지 않으므로
        // 다이얼로그가 열린 상태에서도 트레이 메뉴 → 같은/다른 다이얼로그 재호출이 가능하다.
        if (ModalDialogLoop.IsActive)
        {
            User32.SetForegroundWindow(ModalDialogLoop.ActiveDialog);
            return null;
        }

        _dlgResult = false;
        _dlgClosed = false;
        _checkboxHandles.Clear();
        _scrollChildren.Clear();
        _selectAllState = true;
        _scrollPos = 0;
        _scrollMax = 0;

        // DPI 스케일링
        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTOPRIMARY);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);
        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);

        int pad = DpiHelper.Scale(DlgPadding, dpiScale);
        int checkH = DpiHelper.Scale(DlgCheckHeight, dpiScale);
        int checkGap = DpiHelper.Scale(DlgCheckGap, dpiScale);
        int btnW = DpiHelper.Scale(DlgButtonWidth, dpiScale);
        int btnH = DpiHelper.Scale(DlgButtonHeight, dpiScale);
        int descH = DpiHelper.Scale(DlgDescHeight, dpiScale);
        int sepGap = DpiHelper.Scale(DlgSepGap, dpiScale);
        int itemIndent = DpiHelper.Scale(DlgItemIndent, dpiScale);

        _itemHeight = checkH + checkGap;
        _checkItemX = itemIndent;
        int visibleCount = Math.Min(items.Count, DlgMaxVisibleItems);
        bool needsScroll = items.Count > DlgMaxVisibleItems;
        int scrollbarW = needsScroll
            ? User32.GetSystemMetricsForDpi(Win32Constants.SM_CXVSCROLL, rawDpi) : 0;

        int viewportH = visibleCount * _itemHeight;
        int dlgWidth = DpiHelper.Scale(DlgMinWidth, dpiScale);
        // 비클라이언트 영역 높이 (타이틀바 + 프레임) — 공통 헬퍼 사용
        int nonClientH = Win32DialogHelper.CalculateNonClientHeight(rawDpi);

        int dlgHeight = nonClientH               // non-client (title bar + borders)
            + pad                                // top padding
            + descH + checkGap                   // description label
            + checkH + checkGap                  // "전체 선택"
            + sepGap                             // separator
            + viewportH                          // scrollable item area
            + pad                                // gap before buttons
            + btnH                               // buttons
            + pad;                               // bottom padding

        // UI 폰트 (맑은 고딕 9pt, DPI 스케일) — using 스코프 종료 시 자동 DeleteObject
        using var hFont = Win32DialogHelper.CreateDialogFont(dpiY);
        IntPtr hFontRaw = hFont.DangerousGetHandle();

        // 윈도우 클래스 등록 (한 번만, 중복 등록은 무시됨)
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&CleanupDlgProc,
            lpszClassName = DlgClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref wc);

        var vpWc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&ViewportProc,
            lpszClassName = ViewportClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref vpWc);

        // 화면 중앙 좌표 — 공통 헬퍼 (anchor=null → rcWork 정중앙)
        var (cx, cy) = Win32DialogHelper.CalculateDialogPosition(hMon, dlgWidth, dlgHeight);

        string title = I18n.IsKorean ? "위치 기록 정리" : "Clean position history";
        _hwndDialog = User32.CreateWindowExW(0, DlgClassName, title,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            hwndMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwndDialog == IntPtr.Zero)
            return null;

        // 클라이언트 영역 너비 (비클라이언트 보더 제외)
        int borderW = DpiHelper.Scale(16, dpiScale);
        int contentW = dlgWidth - pad * 2 - borderW;

        // 컨트롤 생성
        int y = pad;

        // 설명 라벨
        string descText = I18n.IsKorean
            ? "삭제할 위치 기록을 선택하세요."
            : "Select position history to delete.";
        IntPtr hwndDesc = User32.CreateWindowExW(0, "STATIC", descText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE,
            pad, y, contentW, descH,
            _hwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndDesc, hFontRaw);
        y += descH + checkGap;

        // "전체 선택" 체크박스
        string selectAllText = I18n.IsKorean ? "전체 선택" : "Select All";
        IntPtr hwndSelectAll = User32.CreateWindowExW(0, "BUTTON", selectAllText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.BS_AUTOCHECKBOX,
            pad, y, contentW, checkH,
            _hwndDialog, (IntPtr)IDC_SELECT_ALL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndSelectAll, hFontRaw);
        User32.SendMessageW(hwndSelectAll, Win32Constants.BM_SETCHECK,
            (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
        y += checkH + checkGap;

        // 구분선 (etched horizontal)
        User32.CreateWindowExW(0, "STATIC", "",
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.SS_ETCHEDHORZ,
            pad, y + sepGap / 2 - 1, contentW, 2,
            _hwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        y += sepGap;

        // 스크롤 뷰포트 (항목 체크박스 컨테이너)
        // WS_CLIPCHILDREN: 부모 페인트·에레이즈에서 자식 영역 제외 (1차 방어).
        // WS_EX_COMPOSITED (dwExStyle): DWM 이 뷰포트와 모든 자식을 오프스크린 비트맵에 합성 후
        // 한 번에 출력하는 더블버퍼링. 썸 드래그·휠 스크롤의 연속 이동 중 중간 상태가
        // 화면에 노출되지 않아 플리커·티어링 제거.
        uint vpStyle = Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_CLIPCHILDREN;
        if (needsScroll) vpStyle |= Win32Constants.WS_VSCROLL;
        _hwndViewport = User32.CreateWindowExW(Win32Constants.WS_EX_COMPOSITED, ViewportClassName, "",
            vpStyle, pad, y, contentW, viewportH,
            _hwndDialog, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _viewportClientH = viewportH;

        // 항목 체크박스 (뷰포트의 자식)
        int checkW = contentW - itemIndent - scrollbarW;
        for (int i = 0; i < items.Count; i++)
        {
            int logicalY = i * _itemHeight;
            IntPtr hwndCheck = User32.CreateWindowExW(0, "BUTTON", items[i],
                Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.BS_AUTOCHECKBOX,
                itemIndent, logicalY, checkW, checkH,
                _hwndViewport, (IntPtr)(IDC_CHECK_BASE + i), IntPtr.Zero, IntPtr.Zero);
            Win32DialogHelper.ApplyFont(hwndCheck, hFontRaw);
            User32.SendMessageW(hwndCheck, Win32Constants.BM_SETCHECK,
                (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
            _checkboxHandles.Add(hwndCheck);
            _scrollChildren.Add((hwndCheck, logicalY));
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

        y += viewportH;

        // 버튼 영역
        y += pad;
        int btnAreaWidth = btnW * 2 + pad;
        int btnX = (dlgWidth - btnAreaWidth) / 2;

        string okText = I18n.IsKorean ? "삭제" : "Delete";
        string cancelText = I18n.IsKorean ? "취소" : "Cancel";
        IntPtr hwndOk = User32.CreateWindowExW(0, "BUTTON", okText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX, y, btnW, btnH,
            _hwndDialog, (IntPtr)IDC_BTN_OK, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndOk, hFontRaw);
        IntPtr hwndCancel = User32.CreateWindowExW(0, "BUTTON", cancelText,
            Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.WS_TABSTOP,
            btnX + btnW + pad, y, btnW, btnH,
            _hwndDialog, (IntPtr)IDC_BTN_CANCEL, IntPtr.Zero, IntPtr.Zero);
        Win32DialogHelper.ApplyFont(hwndCancel, hFontRaw);

        // 모달 표시 — 루프/EnableWindow/포그라운드 복원은 공용 헬퍼에 위임
        User32.ShowWindow(_hwndDialog, Win32Constants.SW_SHOW);

        // 모달 루프 + 결과 수집을 try, 정리는 finally 에서 보장한다. finally 가 _checkboxHandles
        // 를 비우므로 결과 수집은 반드시 try 내부에 유지되어야 한다.
        List<string>? result = null;
        try
        {
            ModalDialogLoop.Run(_hwndDialog, hwndMain, ref _dlgClosed);

            if (_dlgResult)
            {
                result = [];
                for (int i = 0; i < items.Count; i++)
                {
                    if (i < _checkboxHandles.Count)
                    {
                        IntPtr state = User32.SendMessageW(_checkboxHandles[i],
                            Win32Constants.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero);
                        if (state == (IntPtr)Win32Constants.BST_CHECKED)
                            result.Add(items[i]);
                    }
                }
            }
        }
        finally
        {
            // 정리
            User32.DestroyWindow(_hwndDialog);
            _hwndDialog = IntPtr.Zero;
            _hwndViewport = IntPtr.Zero;
            _checkboxHandles.Clear();
            _scrollChildren.Clear();
            // hFont는 using 스코프 종료 시 자동 해제 (SafeFontHandle → DeleteObject)
        }

        return result;
    }

    // ================================================================
    // 스크롤
    // ================================================================

    private static void ScrollTo(int newPos)
    {
        newPos = Math.Clamp(newPos, 0, _scrollMax);
        if (newPos == _scrollPos) return;

        int dy = _scrollPos - newPos;  // 위로 스크롤(newPos↑) = 콘텐츠 위로 이동 = dy 음수
        _scrollPos = newPos;

        var si = new SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = Win32Constants.SIF_POS,
            nPos = newPos,
        };
        User32.SetScrollInfo(_hwndViewport, Win32Constants.SB_VERT, ref si, true);

        // 자식 체크박스 HWND 들을 SW_SCROLLCHILDREN 으로 OS가 한 번에 이동시키고,
        // 노출된 띠만 SW_INVALIDATE|SW_ERASE 로 무효화 → 기존 N회 SetWindowPos + 전체 InvalidateRect 대체.
        User32.ScrollWindowEx(_hwndViewport, 0, dy,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            Win32Constants.SW_SCROLLCHILDREN | Win32Constants.SW_INVALIDATE | Win32Constants.SW_ERASE);
    }

    private static int ResolveVScrollPosition(IntPtr hwnd, int scrollCode)
    {
        int lineStep = _itemHeight;
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

    private static void HandleMouseWheel(IntPtr wParam)
    {
        short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        int steps = delta / Win32Constants.WHEEL_DELTA;
        ScrollTo(_scrollPos - steps * WheelLineStep * _itemHeight);
    }

    // ================================================================
    // WndProc — 다이얼로그
    // ================================================================

    [UnmanagedCallersOnly]
    private static IntPtr CleanupDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == IDC_BTN_OK || id == Win32Constants.IDOK)
                {
                    _dlgResult = true;
                    _dlgClosed = true;
                    return IntPtr.Zero;
                }
                // IsDialogMessageW 가 ESC 를 WM_COMMAND wParam=IDCANCEL(2) 로 변환해 보낸다.
                // 취소 버튼 ID(IDC_BTN_CANCEL=5502)와 별개로 IDCANCEL 도 수락해야 ESC 가 작동.
                if (id == IDC_BTN_CANCEL || id == Win32Constants.IDCANCEL)
                {
                    _dlgResult = false;
                    _dlgClosed = true;
                    return IntPtr.Zero;
                }
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

            case Win32Constants.WM_MOUSEWHEEL:
                // 뷰포트 외부(버튼 등)에 포커스가 있을 때도 스크롤 동작
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
