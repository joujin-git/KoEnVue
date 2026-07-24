using KoEnVue.Core.Native;

namespace KoEnVue.Core.Windowing;

/// <summary>
/// 주기적으로 SetWindowPos(HWND_TOPMOST) 를 재적용하기 위한 단일 WM_TIMER 워치독.
/// <para>
/// 다른 풀스크린 / topmost 윈도우가 자기를 위에 깔거나 셸이 z-order 를 갱신해도
/// 플로팅 배지가 항상 최상단에 머무르도록 한다. OverlayAnimator 가 담당하던 페이드 /
/// 슬라이드 / 하이라이트 / 홀드 / topmost 5 트랙 중 본 트랙만 시간 / 상태 의존성이
/// 없어 분리해도 회귀 위험이 최소 — 나머지 4 트랙의 분해는 보류 (dev-notes 참조).
/// </para>
/// <para>
/// 타이머 ID 는 호출자가 제공한다 (Core 가 ID 충돌 책임을 지지 않도록). 호출자는
/// WM_TIMER 도착 시 <see cref="TryHandleTimer"/> 에 위임하면 본 워치독이 자기 ID 만
/// 골라 onTick 콜백을 실행한다.
/// </para>
/// </summary>
public sealed class TopmostWatchdog : IDisposable
{
    private readonly IntPtr _hwndTimer;
    private readonly nuint _timerId;
    private readonly Action _onTick;
    private int _intervalMs;

    /// <summary>
    /// 인스턴스 생성과 동시에 <paramref name="initialIntervalMs"/> &gt; 0 이면 타이머 등록.
    /// </summary>
    public TopmostWatchdog(IntPtr hwndTimer, nuint timerId, int initialIntervalMs, Action onTick)
    {
        _hwndTimer = hwndTimer;
        _timerId = timerId;
        _onTick = onTick;
        SetInterval(initialIntervalMs);
    }

    public void Dispose() => Stop();

    /// <summary>타이머 정지 (KillTimer).</summary>
    public void Stop() => User32.KillTimer(_hwndTimer, _timerId);

    /// <summary>
    /// 주기 변경. 0 또는 음수면 정지. 같은 값으로 재호출해도 KillTimer + SetTimer 사이클이
    /// 일어나므로 호출자는 변화 가드를 두는 게 좋다 (OverlayAnimator 가 그렇게 함).
    /// </summary>
    public void SetInterval(int intervalMs)
    {
        _intervalMs = intervalMs;
        User32.KillTimer(_hwndTimer, _timerId);
        if (intervalMs > 0)
            User32.SetTimer(_hwndTimer, _timerId, (uint)intervalMs, IntPtr.Zero);
    }

    /// <summary>현재 주기 (ms). 0 이면 정지 상태.</summary>
    public int IntervalMs => _intervalMs;

    /// <summary>
    /// WM_TIMER 디스패치 헬퍼. <paramref name="timerId"/> 가 본 워치독의 것이면 onTick 호출 후
    /// true 를 반환한다. 호출자는 이 결과로 자기 디스패치 체인을 short-circuit 할 수 있다.
    /// </summary>
    public bool TryHandleTimer(nuint timerId)
    {
        if (timerId != _timerId) return false;
        _onTick();
        return true;
    }
}
