using System.Runtime.InteropServices;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.UI.Dialogs;

/// <summary>
/// SettingsDialog 의 스크롤/뷰포트 분할.
/// 스크롤 상태 필드, 스크롤 메서드(SetupScrollbar/ScrollTo/ScrollFieldIntoView),
/// SB_* 코드 해석(ResolveVScrollPosition), 뷰포트 WndProc(ViewportProc).
/// </summary>
internal static partial class SettingsDialog
{
    // ================================================================
    // 스크롤 상태
    // ================================================================

    private static int _scrollPos;
    private static int _scrollMax;
    private static int _viewportClientH;
    private static int _lineHeight;

    // 스크롤 자식 컨트롤 추적: (Hwnd, X, LogicalY)
    private static readonly List<(IntPtr Hwnd, int X, int LogicalY)> _scrollChildren = new();

    // ================================================================
    // 스크롤 메서드
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
    /// </summary>
    private static void ScrollTo(int newPos)
        => ScrollableDialogHelper.ScrollTo(_hwndViewport, ref _scrollPos, _scrollMax, newPos);

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

    /// <summary>SB_* 스크롤 코드를 목표 스크롤 위치로 해석.</summary>
    private static int ResolveVScrollPosition(IntPtr hwnd, int scrollCode)
        => ScrollableDialogHelper.ResolveVScrollPosition(
            hwnd, scrollCode, _scrollPos, _scrollMax, _viewportClientH, _lineHeight);

    // ================================================================
    // 뷰포트 WndProc
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
            {
                ScrollTo(ScrollableDialogHelper.CalculateWheelScrollPos(wParam, _scrollPos, _lineHeight));
                return IntPtr.Zero;
            }

            default:
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }
}
