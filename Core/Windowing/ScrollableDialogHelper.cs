using System.Runtime.InteropServices;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// 세로 스크롤 다이얼로그(SettingsDialog / CleanupDialog) 공용 스크롤 계산·WinAPI 호출 헬퍼.
/// 두 다이얼로그가 공유하는 패턴 3종:
///   1) SIF_POS 갱신 + ScrollWindowEx(SW_SCROLLCHILDREN|SW_INVALIDATE|SW_ERASE) 로 자식 일괄 이동
///   2) SB_* 코드 → 목표 위치 (line/page/top/bottom/thumb)
///   3) WM_MOUSEWHEEL delta → steps × lineStep
/// 상태(scrollPos 등)는 호출자가 소유하고 <c>ref</c> 또는 값으로 넘긴다. 헬퍼는 순수 유틸.
/// </summary>
internal static class ScrollableDialogHelper
{
    /// <summary>마우스 휠 한 틱당 스크롤할 "라인" 수. Windows 기본 3줄 스크롤과 정렬.</summary>
    public const int WheelLineStep = 3;

    /// <summary>
    /// 스크롤 위치를 <paramref name="newPos"/> 로 갱신하고 SIF_POS 반영 + 자식 이동을 수행.
    /// <paramref name="scrollPos"/> 는 ref 로 받아 호출자 상태와 동기화.
    /// 이미 같은 위치면 no-op 으로 <c>false</c>, 실제 이동했으면 <c>true</c>.
    /// </summary>
    public static bool ScrollTo(IntPtr hwndViewport, ref int scrollPos, int scrollMax, int newPos)
    {
        newPos = Math.Clamp(newPos, 0, scrollMax);
        if (newPos == scrollPos) return false;

        int dy = scrollPos - newPos;  // 위로 스크롤(newPos↑) = 콘텐츠 위로 이동 = dy 음수
        scrollPos = newPos;

        var si = new SCROLLINFO
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = Win32Constants.SIF_POS,
            nPos = newPos,
        };
        User32.SetScrollInfo(hwndViewport, Win32Constants.SB_VERT, ref si, true);

        // N개의 자식 컨트롤을 SW_SCROLLCHILDREN 으로 OS가 한 번에 이동시키고
        // 노출된 띠만 SW_INVALIDATE|SW_ERASE 로 무효화 → SetWindowPos 루프 + 전체 InvalidateRect 대체.
        User32.ScrollWindowEx(hwndViewport, 0, dy,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            Win32Constants.SW_SCROLLCHILDREN | Win32Constants.SW_INVALIDATE | Win32Constants.SW_ERASE);
        return true;
    }

    /// <summary>
    /// WM_VSCROLL 의 SB_* 코드를 다음 스크롤 위치로 해석.
    /// 알 수 없는 코드는 현재 위치를 그대로 반환해 ScrollTo 가 no-op 이 되도록 한다.
    /// </summary>
    public static int ResolveVScrollPosition(IntPtr hwnd, int scrollCode,
        int scrollPos, int scrollMax, int viewportClientH, int lineHeight)
    {
        int lineStep = lineHeight;
        int pageStep = viewportClientH > lineStep ? viewportClientH - lineStep : lineStep * 5;

        switch (scrollCode)
        {
            case Win32Constants.SB_LINEUP: return scrollPos - lineStep;
            case Win32Constants.SB_LINEDOWN: return scrollPos + lineStep;
            case Win32Constants.SB_PAGEUP: return scrollPos - pageStep;
            case Win32Constants.SB_PAGEDOWN: return scrollPos + pageStep;
            case Win32Constants.SB_TOP: return 0;
            case Win32Constants.SB_BOTTOM: return scrollMax;
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
                    : scrollPos;
            }
            default: return scrollPos;
        }
    }

    /// <summary>
    /// WM_MOUSEWHEEL 의 wParam 에서 delta → 목표 scrollPos 계산.
    /// 부호: 위로 회전 = 양수 → 스크롤 위로 = scrollPos 감소(화면상 콘텐츠가 아래로).
    /// </summary>
    public static int CalculateWheelScrollPos(IntPtr wParam, int scrollPos, int lineHeight)
    {
        short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        int steps = delta / Win32Constants.WHEEL_DELTA;
        return scrollPos - steps * WheelLineStep * lineHeight;
    }
}
