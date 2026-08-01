using System.Reflection;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 드래그 중 숨김 보류 (AUDIT-2026-07-30 §M).
///
/// <para>
/// 시스템 sizemove 모달 루프(<c>DefWindowProc</c> 내부의 자체 메시지 펌프)는 <c>WM_TIMER</c> 도 계속
/// 디스패치한다. 그래서 표시 시간이 만료된 애니메이션 타이머가 <b>사용자가 붙잡고 드래그하는 중인
/// 배지를 그 자리에서 지워버렸다.</b> <c>UpdateOverlay</c> 에는 <c>_isDragging</c> 가드가 있어 위치는
/// 지켜졌지만 <c>Hide()</c> 에는 없어서, 위치는 남고 창만 사라지는 비대칭이었다.
/// </para>
///
/// <para>
/// 단순히 드래그 중 <c>Hide</c> 를 무시하면 "시간이 지나면 숨는다"는 반대편 의도가 깨지므로,
/// 보류했다가 <c>EndDrag</c> 에서 적용하는지까지 함께 고정한다.
/// </para>
///
/// <para>
/// HWND 는 <see cref="IntPtr.Zero"/> 로 둔다 — <c>Hide</c> 는 그 경우 <c>ShowWindow</c> 를 건너뛰고
/// 가시 플래그만 다루므로, 실제 창 없이 상태 전이만 정확히 관측할 수 있다.
/// </para>
/// </summary>
public class DragHideDeferralTests
{
    /// <summary>렌더 콜백은 호출되지 않는다(Show/Render 경로를 타지 않음). 크기만 형식적으로 반환.</summary>
    private static LayeredOverlayBase NewEngine() =>
        new(IntPtr.Zero, static (_, _, _) => (0, 0));

    /// <summary>Show() 는 Win32 를 타므로, 가시 상태만 직접 세워 "보이는 중" 을 만든다.</summary>
    private static void ForceVisible(LayeredOverlayBase engine) =>
        typeof(LayeredOverlayBase)
            .GetField("_isVisible", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, true);

    [Fact]
    public void 드래그_중_Hide_는_보류된다()
    {
        using var engine = NewEngine();
        ForceVisible(engine);

        engine.BeginDrag(snapToWindows: false);
        engine.Hide();

        // 가드가 없으면 여기서 이미 false — 사용자가 잡고 있는 배지가 사라진 상태.
        Assert.True(engine.IsVisible);
    }

    [Fact]
    public void 드래그가_끝나면_보류된_Hide_가_적용된다()
    {
        using var engine = NewEngine();
        ForceVisible(engine);

        engine.BeginDrag(snapToWindows: false);
        engine.Hide();
        engine.EndDrag();

        // 보류를 그냥 버리면 "시간이 지나면 숨는다"가 깨져 배지가 영영 남는다.
        Assert.False(engine.IsVisible);
    }

    [Fact]
    public void 드래그가_아니면_Hide_는_즉시_적용된다()
    {
        using var engine = NewEngine();
        ForceVisible(engine);

        engine.Hide();

        Assert.False(engine.IsVisible);
    }

    [Fact]
    public void 보류_없이_끝난_드래그는_가시_상태를_바꾸지_않는다()
    {
        using var engine = NewEngine();
        ForceVisible(engine);

        engine.BeginDrag(snapToWindows: false);
        engine.EndDrag();

        // EndDrag 가 보류 플래그와 무관하게 Hide 를 부르면 드래그만 해도 배지가 사라진다.
        Assert.True(engine.IsVisible);
    }
}
