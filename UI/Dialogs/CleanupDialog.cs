using System.Runtime.InteropServices;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.UI.Dialogs;

/// <summary>
/// 미사용 indicator_positions 항목을 체크박스 UI로 선택 삭제하는 Win32 모달 다이얼로그.
/// Tray.cs에서 분리 (Wave 1-D) — 공용 Win32 다이얼로그 헬퍼와 함께 사용.
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

    // 다이얼로그 컨트롤 ID
    private const int IDC_CHECK_BASE = 5000;
    private const int IDC_SELECT_ALL = 5500;
    private const int IDC_BTN_OK = 5501;
    private const int IDC_BTN_CANCEL = 5502;

    // Win32 다이얼로그 윈도우 클래스 이름
    private const string DlgClassName = "KoEnVueCleanupDlg";

    // ================================================================
    // 다이얼로그 상태 (모달 루프용)
    // ================================================================
    private static bool _dlgResult;
    private static bool _dlgClosed;
    private static IntPtr _hwndDialog;
    private static readonly List<IntPtr> _checkboxHandles = [];
    private static bool _selectAllState = true;

    /// <summary>
    /// 체크박스 선택 다이얼로그를 표시하고 선택된 항목을 반환한다. 취소 시 null.
    /// </summary>
    public static unsafe List<string>? Show(IntPtr hwndMain, List<string> items)
    {
        _dlgResult = false;
        _dlgClosed = false;
        _checkboxHandles.Clear();
        _selectAllState = true;

        // DPI 스케일링
        User32.GetCursorPos(out POINT cursorPt);
        IntPtr hMon = User32.MonitorFromPoint(cursorPt, Win32Constants.MONITOR_DEFAULTTOPRIMARY);
        double dpiScale = DpiHelper.GetScale(hMon);
        var (_, dpiY) = DpiHelper.GetRawDpi(hMon);

        int pad = DpiHelper.Scale(DlgPadding, dpiScale);
        int checkH = DpiHelper.Scale(DlgCheckHeight, dpiScale);
        int checkGap = DpiHelper.Scale(DlgCheckGap, dpiScale);
        int btnW = DpiHelper.Scale(DlgButtonWidth, dpiScale);
        int btnH = DpiHelper.Scale(DlgButtonHeight, dpiScale);
        int descH = DpiHelper.Scale(DlgDescHeight, dpiScale);
        int sepGap = DpiHelper.Scale(DlgSepGap, dpiScale);
        int itemIndent = DpiHelper.Scale(DlgItemIndent, dpiScale);

        int visibleCount = Math.Min(items.Count, DlgMaxVisibleItems);
        int checkAreaHeight = visibleCount * (checkH + checkGap);
        int dlgWidth = DpiHelper.Scale(DlgMinWidth, dpiScale);
        // 비클라이언트 영역 높이 (타이틀바 + 프레임) — 공통 헬퍼 사용
        uint rawDpi = (uint)Math.Round(dpiScale * DpiHelper.BASE_DPI);
        int nonClientH = Win32DialogHelper.CalculateNonClientHeight(rawDpi);

        int dlgHeight = nonClientH               // non-client (title bar + borders)
            + pad                                // top padding
            + descH + checkGap                   // description label
            + checkH + checkGap                  // "전체 선택"
            + sepGap                             // separator
            + checkAreaHeight                    // items
            + pad                                // gap before buttons
            + btnH                               // buttons
            + pad;                               // bottom padding

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

        // 다이얼로그 윈도우 클래스 등록 (한 번만)
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&CleanupDlgProc,
            lpszClassName = DlgClassName,
            hbrBackground = (IntPtr)(Win32Constants.COLOR_BTNFACE + 1),
        };
        User32.RegisterClassExW(ref wc); // 중복 등록은 무시됨

        // 화면 중앙 좌표
        MONITORINFOEXW mi = default;
        mi.cbSize = (uint)Marshal.SizeOf<MONITORINFOEXW>();
        User32.GetMonitorInfoW(hMon, ref mi);
        int cx = (mi.rcWork.Left + mi.rcWork.Right - dlgWidth) / 2;
        int cy = (mi.rcWork.Top + mi.rcWork.Bottom - dlgHeight) / 2;

        string title = I18n.IsKorean ? "미사용 위치 데이터 정리" : "Clean unused position data";
        _hwndDialog = User32.CreateWindowExW(0, DlgClassName, title,
            Win32Constants.WS_CAPTION | Win32Constants.WS_SYSMENU,
            cx, cy, dlgWidth, dlgHeight,
            hwndMain, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwndDialog == IntPtr.Zero)
        {
            return null;
        }

        // 클라이언트 영역 너비 (비클라이언트 보더 제외)
        int borderW = DpiHelper.Scale(16, dpiScale);
        int contentW = dlgWidth - pad * 2 - borderW;

        // 컨트롤 생성
        int y = pad;

        // 설명 라벨
        string descText = I18n.IsKorean
            ? "삭제할 위치 데이터를 선택하세요."
            : "Select position data to delete.";
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

        // 항목 체크박스
        for (int i = 0; i < items.Count; i++)
        {
            IntPtr hwndCheck = User32.CreateWindowExW(0, "BUTTON", items[i],
                Win32Constants.WS_CHILD | Win32Constants.WS_VISIBLE | Win32Constants.BS_AUTOCHECKBOX,
                pad + itemIndent, y, contentW - itemIndent, checkH,
                _hwndDialog, (IntPtr)(IDC_CHECK_BASE + i), IntPtr.Zero, IntPtr.Zero);
            Win32DialogHelper.ApplyFont(hwndCheck, hFontRaw);
            User32.SendMessageW(hwndCheck, Win32Constants.BM_SETCHECK,
                (IntPtr)Win32Constants.BST_CHECKED, IntPtr.Zero);
            _checkboxHandles.Add(hwndCheck);
            y += checkH + checkGap;
        }

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

        // 모달 표시
        User32.EnableWindow(hwndMain, false);
        User32.ShowWindow(_hwndDialog, Win32Constants.SW_SHOW);

        // 모달 메시지 루프
        while (!_dlgClosed && User32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (!User32.IsDialogMessageW(_hwndDialog, ref msg))
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }

        // 결과 수집
        List<string>? result = null;
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

        // 정리
        User32.EnableWindow(hwndMain, true);
        User32.SetForegroundWindow(hwndMain);
        User32.DestroyWindow(_hwndDialog);
        _hwndDialog = IntPtr.Zero;
        _checkboxHandles.Clear();
        // hFont는 using 스코프 종료 시 자동 해제 (SafeFontHandle → DeleteObject)

        return result;
    }

    [UnmanagedCallersOnly]
    private static IntPtr CleanupDlgProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_COMMAND:
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                if (id == IDC_BTN_OK)
                {
                    _dlgResult = true;
                    _dlgClosed = true;
                    return IntPtr.Zero;
                }
                if (id == IDC_BTN_CANCEL)
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

            case Win32Constants.WM_CLOSE:
                _dlgResult = false;
                _dlgClosed = true;
                return IntPtr.Zero;

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
