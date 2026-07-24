using System.Runtime.InteropServices;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// 드래그 중인 윈도우 엣지를 다른 가시 창의 엣지에 스냅하기 위한 유틸.
/// <para>
/// <see cref="CollectTargets(IntPtr)"/> 가 EnumWindows 로 후보 RECT 를 캐싱하고,
/// <see cref="ApplySnap"/> 이 movingRect 와 각 후보의 4 엣지 쌍을 검사해 최단 거리 스냅을 적용한다.
/// 현재 위치의 모니터 work area 도 후보로 포함 (간격 0 — 화면 엣지 스냅).
/// </para>
/// <para>
/// 정적 캐시 (s_targets / s_ownerHwnd) 는 한 프로세스에 동시에 드래그 중인 플로팅 배지가
/// 하나뿐이라는 가정 — LayeredOverlayBase 인스턴스가 앱당 1개임에 의존한다.
/// <see cref="EnumWindowsCallback"/> 는 <c>[UnmanagedCallersOnly]</c> 라 인스턴스 필드에
/// 접근할 수 없어 정적 브리지가 필수.
/// </para>
/// </summary>
internal static class WindowSnapHelper
{
    private static readonly List<RECT> s_targets = new(64);
    private static IntPtr s_ownerHwnd;

    /// <summary>
    /// 스냅 후보 최소 크기 (DPI 미적용 px). <c>[UnmanagedCallersOnly]</c> 정적 콜백에서
    /// 인스턴스 필드 접근 불가라 본 유틸 내부 private const 로 보관.
    /// </summary>
    private const int MinWindowSizePx = 80;

    /// <summary>
    /// 드래그 시작 — 가시 / non-cloaked / non-iconic 한 다른 창의 시각 프레임 RECT 를
    /// EnumWindows 로 수집한다. <paramref name="ownerHwnd"/> 는 자기 자신을 후보에서 제외하기 위함.
    /// </summary>
    public static unsafe void CollectTargets(IntPtr ownerHwnd)
    {
        s_targets.Clear();
        s_ownerHwnd = ownerHwnd;
        User32.EnumWindows(&EnumWindowsCallback, IntPtr.Zero);
    }

    /// <summary>드래그 종료 — 캐시된 후보 RECT 와 owner 핸들을 비운다.</summary>
    public static void ClearTargets()
    {
        s_targets.Clear();
        s_ownerHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// <paramref name="movingRect"/> 와 각 후보 RECT 의 엣지 쌍을 검사해 가장 가까운
    /// 스냅 후보를 찾아 RECT 를 이동시킨다. X / Y 축 독립 처리, 잠긴 축은 스킵.
    /// 타겟 RECT 와의 수직 / 수평 겹침을 요구해 "멀리 떨어진 창" 에 끌려가는 현상을 방지.
    /// work area 는 인디가 항상 그 안에 있어 겹침 체크가 항상 통과 → 화면 엣지 스냅 성립.
    /// <paramref name="snapThresholdPx"/> / <paramref name="snapGapPx"/> 는 logical px 로
    /// 본 메서드 내부에서 <paramref name="dpiScale"/> 로 물리 px 변환된다.
    /// </summary>
    /// <returns>스냅이 발생해 RECT 가 수정됐으면 true.</returns>
    public static bool ApplySnap(
        ref RECT movingRect, bool xLocked, bool yLocked,
        double dpiScale, int snapThresholdPx, int snapGapPx)
    {
        int threshold = DpiHelper.Scale(snapThresholdPx, dpiScale);
        int gap = DpiHelper.Scale(snapGapPx, dpiScale);
        int w = movingRect.Right - movingRect.Left;
        int h = movingRect.Bottom - movingRect.Top;

        int bestDx = 0, bestDy = 0;
        int bestDistX = threshold + 1;
        int bestDistY = threshold + 1;

        // 현재 위치의 모니터 work area 추가 (화면 엣지 스냅 — 간격 없음)
        IntPtr hMonitor = DpiHelper.GetMonitorFromPoint(
            movingRect.Left + w / 2, movingRect.Top + h / 2);
        RECT workArea = DpiHelper.GetWorkArea(hMonitor);
        ConsiderTarget(ref movingRect, workArea, xLocked, yLocked,
            ref bestDx, ref bestDy, ref bestDistX, ref bestDistY, gap: 0);

        // 창 엣지 스냅 — 경계선 겹침 방지 간격 적용
        foreach (RECT target in s_targets)
        {
            ConsiderTarget(ref movingRect, target, xLocked, yLocked,
                ref bestDx, ref bestDy, ref bestDistX, ref bestDistY, gap);
        }

        bool modified = false;
        if (bestDistX <= threshold)
        {
            movingRect.Left += bestDx;
            movingRect.Right += bestDx;
            modified = true;
        }
        if (bestDistY <= threshold)
        {
            movingRect.Top += bestDy;
            movingRect.Bottom += bestDy;
            modified = true;
        }
        return modified;
    }

    /// <summary>
    /// 단일 타겟 rect에 대해 4개 엣지 쌍(L↔L, L↔R, R↔L, R↔R)과
    /// (T↔T, T↔B, B↔T, B↔B)을 검사해 최단 거리 스냅 후보를 갱신.
    /// Y 겹침이 있는 경우에만 X 엣지 스냅, X 겹침이 있는 경우에만 Y 엣지 스냅.
    /// </summary>
    private static void ConsiderTarget(
        ref RECT movingRect, RECT target,
        bool xLocked, bool yLocked,
        ref int bestDx, ref int bestDy,
        ref int bestDistX, ref int bestDistY,
        int gap)
    {
        bool yOverlap = movingRect.Top < target.Bottom && movingRect.Bottom > target.Top;
        if (!xLocked && yOverlap)
        {
            // gap 부호: inside(같은 쪽 엣지)는 안쪽으로, outside(반대 쪽)는 바깥으로
            TryEdge(target.Left + gap - movingRect.Left, ref bestDistX, ref bestDx);    // L-L inside
            TryEdge(target.Left - gap - movingRect.Right, ref bestDistX, ref bestDx);   // L-R outside
            TryEdge(target.Right + gap - movingRect.Left, ref bestDistX, ref bestDx);   // R-L outside
            TryEdge(target.Right - gap - movingRect.Right, ref bestDistX, ref bestDx);  // R-R inside
        }

        bool xOverlap = movingRect.Left < target.Right && movingRect.Right > target.Left;
        if (!yLocked && xOverlap)
        {
            TryEdge(target.Top + gap - movingRect.Top, ref bestDistY, ref bestDy);      // T-T inside
            TryEdge(target.Top - gap - movingRect.Bottom, ref bestDistY, ref bestDy);   // T-B outside
            TryEdge(target.Bottom + gap - movingRect.Top, ref bestDistY, ref bestDy);   // B-T outside
            TryEdge(target.Bottom - gap - movingRect.Bottom, ref bestDistY, ref bestDy);// B-B inside
        }
    }

    private static void TryEdge(int delta, ref int bestDist, ref int bestDelta)
    {
        int abs = Math.Abs(delta);
        if (abs < bestDist)
        {
            bestDist = abs;
            bestDelta = delta;
        }
    }

    /// <summary>
    /// EnumWindows 콜백. <see cref="CollectTargets"/> 에서 스냅 후보 RECT 를 s_targets 에 수집.
    /// <c>[UnmanagedCallersOnly]</c> + 함수 포인터 방식 (NativeAOT 권장).
    /// BOOL 리턴 (1 = 계속 열거, 0 = 중단).
    /// </summary>
    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (hwnd == s_ownerHwnd) return 1;
            if (!User32.IsWindowVisible(hwnd)) return 1;
            if (User32.IsIconic(hwnd)) return 1;
            if (Dwmapi.IsCloaked(hwnd)) return 1;
            if (!Dwmapi.TryGetVisibleFrame(hwnd, out RECT frame)) return 1;

            int w = frame.Right - frame.Left;
            int h = frame.Bottom - frame.Top;
            // 최소 크기 필터 — [UnmanagedCallersOnly] 정적 콜백이라 인스턴스 필드 접근 불가.
            if (w < MinWindowSizePx || h < MinWindowSizePx)
                return 1;

            s_targets.Add(frame);
            return 1;
        }
        catch (Exception ex)
        {
            // [UnmanagedCallersOnly] 콜백에서 관리 예외가 unmanaged 경계(EnumWindows)를 넘으면
            // NativeAOT 런타임이 프로세스를 종료시킨다. 이 창을 스냅 후보에서 누락하고 열거를
            // 계속하는 것이 안전한 복구 — 드래그 스냅은 best-effort 기능.
            LogProvider.Sink?.Debug($"Snap target enumeration skipped a window: {ex.Message}");
            return 1;
        }
    }
}
