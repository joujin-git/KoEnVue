using System.Reflection;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 배지 가시성 상태 기계 (bug-hunt 2026-08-02 G14 — 확정 #18).
///
/// <para>
/// 감지 루프의 tick 간 래치 <c>WindowMoving</c> 은 config 교체를 인지하지 못했다. 창 이동을 감지하면
/// 배지를 숨기고 래치를 세운 뒤 <b>이동이 멎으면</b> 되살리는 구조인데, 되살리는 쪽이
/// <c>PositionMode == Window</c> 가드 <b>뒤</b>에 있었다. 그래서 「숨김」과 「복구」 사이에
/// <c>position_mode</c> 가 <c>window → fixed</c> 로 바뀌면 복구 경로 자체가 사라진다.
/// </para>
///
/// <para>
/// 이 경로는 <b>Win32 에 닿지 않는다</b> — 모드 가드는 <c>Dwmapi.TryGetVisibleFrame</c> 보다 앞이라
/// 실제 창 없이 결정적으로 검증할 수 있다. 나머지 가시성 결함(G4·G19)은 애니메이터와 실제 창을
/// 요구해 단위 테스트가 불가능하며, invariant grep 과 문서로만 고정한다.
/// </para>
/// </summary>
public class IndicatorVisibilityTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly Type StateType =
        typeof(DetectionService).GetNestedType("DetectionState", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("DetectionService.DetectionState not found — 타입명이 바뀌었으면 테스트도 갱신할 것.");

    private static readonly MethodInfo TrackWindowMove =
        typeof(DetectionService).GetMethod("TrackWindowMove", PrivateStatic)
        ?? throw new InvalidOperationException("DetectionService.TrackWindowMove not found.");

    /// <summary>모드 가드 경로에서는 어느 콜백도 호출되지 않으므로 최소 구성으로 채운다.</summary>
    private static DetectionHost NewHost() => new()
    {
        GetConfig = static () => new AppConfig(),
        GetHwndMain = static () => IntPtr.Zero,
        GetHwndOverlay = static () => IntPtr.Zero,
        GetHwndCursorOverlay = static () => IntPtr.Zero,
        IsIndicatorVisible = static () => false,
        IsSessionLocked = static () => false,
        IsStopping = static () => false,
    };

    private static object NewState(bool windowMoving)
    {
        object state = Activator.CreateInstance(StateType)!;
        StateType.GetField("WindowMoving")!.SetValue(state, windowMoving);
        StateType.GetField("LastForegroundProcessName")!.SetValue(state, "");
        return state;
    }

    private static bool WindowMovingOf(object state) =>
        (bool)StateType.GetField("WindowMoving")!.GetValue(state)!;

    /// <summary>ref 파라미터 둘(state · foregroundChanged)을 boxed 로 넘기고 호출 후 되읽는다.</summary>
    private static (object state, bool foregroundChanged) Invoke(
        object state, PositionMode mode, bool foregroundChanged)
    {
        object[] args =
        [
            NewHost(),
            state,
            IntPtr.Zero,
            new AppConfig() with { PositionMode = mode },
            foregroundChanged,
        ];
        TrackWindowMove.Invoke(null, args);
        return (args[1], (bool)args[4]);
    }

    [Fact]
    public void 창_모드가_아니게_바뀌면_이동_래치가_풀린다()
    {
        // 래치가 굳으면 그 틱들에서 foregroundChanged 는 false(같은 hwnd·미필터)라 아무것도 post 되지
        // 않고, 메인 쪽 자기치유도 막힌다 — RefreshVisibleIndicator 는 _indicatorVisible 이 false 라
        // no-op 이다. 사용자가 창을 바꾸거나 한/영을 토글할 때까지 배지가 숨겨진 채 남았다.
        var (state, foregroundChanged) = Invoke(NewState(windowMoving: true), PositionMode.Fixed, false);

        Assert.False(WindowMovingOf(state), "모드가 바뀌면 래치를 풀어야 한다");
        Assert.True(foregroundChanged, "래치를 풀 때 배지 복구를 함께 유발해야 한다");
    }

    [Fact]
    public void 창_모드가_아니고_래치도_없으면_아무_일도_하지_않는다()
    {
        // 반대 방향 가드 — 모드 가드에서 무조건 복구를 유발하면 fixed 모드의 매 틱마다 불필요한
        // 위치 갱신이 돈다. 래치가 서 있을 때만 풀어야 한다.
        var (state, foregroundChanged) = Invoke(NewState(windowMoving: false), PositionMode.Fixed, false);

        Assert.False(WindowMovingOf(state));
        Assert.False(foregroundChanged, "래치가 없으면 복구를 유발하면 안 된다");
    }

    [Fact]
    public void 이미_설정된_foregroundChanged_는_보존된다()
    {
        // 호출자가 세운 값을 덮어 내리면 상위 로직의 판정이 사라진다.
        var (_, foregroundChanged) = Invoke(NewState(windowMoving: false), PositionMode.Fixed, true);

        Assert.True(foregroundChanged);
    }
}
