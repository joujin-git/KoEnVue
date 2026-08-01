using System.Reflection;
using KoEnVue.App.Models;
using KoEnVue.App.UI.Dialogs;
using KoEnVue.Core.Windowing;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 모달 다이얼로그 재진입 가드 (AUDIT-2026-07-30 §A) 회귀 고정.
///
/// <para>
/// 결함의 실체는 "재진입 시 두 번째 창이 안 뜬다"가 아니라 <b>두 번째 호출이 살아 있는 첫 창의
/// 정적 상태를 파괴한다</b>는 것이었다. <c>DialogShell.Run</c> 안의 가드는 프롤로그가 이미 상태를
/// 덮은 뒤에 평가되고, <c>Run</c> 이 false 를 반환해도 에필로그가 그대로 실행돼 마무리로 상태를
/// 무효화한다. 그래서 이 테스트는 반환값이 아니라 <b>정적 필드의 보존</b>을 검사한다 — 가드를
/// <c>Show()</c> 첫 문장에서 빼면 실패한다.
/// </para>
///
/// <para>
/// 활성 모달 상태는 <see cref="ModalDialogLoop.RunExternal"/> 로 만든다. 이 경로는 메시지 펌프도
/// 창 생성도 하지 않고 활성 표식만 세우므로, 실제 Win32 다이얼로그를 띄우지 않고 재진입 시점을
/// 그대로 재현할 수 있다. <see cref="IntPtr.Zero"/> 를 넘기면 내부 센티넬로 치환돼
/// <c>SetForegroundWindow</c> 호출조차 일어나지 않는다.
/// </para>
///
/// <para>
/// 정적 필드 접근은 리플렉션을 쓴다 — 검사 대상이 "외부에서 관측할 필요가 없는 모달 내부 상태"라
/// 프로덕션 코드에 테스트 전용 노출을 만들지 않기 위함이다 (테스트 csproj 는 reflection 전제).
/// </para>
/// </summary>
[Collection(SettingsDialogStaticsCollection.Name)]
public class ModalReentryGuardTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static FieldInfo Field(Type owner, string name) =>
        owner.GetField(name, PrivateStatic)
            ?? throw new InvalidOperationException($"{owner.Name}.{name} not found — 필드명이 바뀌었으면 테스트도 갱신할 것.");

    /// <summary>모달이 활성인 상태에서 <paramref name="duringModal"/> 을 실행한다.</summary>
    private static void WhileModalActive(Action duringModal) =>
        ModalDialogLoop.RunExternal(IntPtr.Zero, duringModal);

    // 소유자 HWND 자리 — 가드가 조기 반환하므로 Win32 에 도달하지 않는다.
    private static readonly IntPtr DummyOwner = (IntPtr)0x1234;

    // ================================================================
    // 판정 자체
    // ================================================================

    [Fact]
    public void RejectReentry_모달이_없으면_false()
    {
        Assert.False(ModalDialogLoop.IsActive);
        Assert.False(ModalDialogLoop.RejectReentry());
    }

    [Fact]
    public void RejectReentry_모달_중에는_true_이고_종료_후_복원된다()
    {
        WhileModalActive(() =>
        {
            Assert.True(ModalDialogLoop.IsActive);
            Assert.True(ModalDialogLoop.RejectReentry());
        });

        Assert.False(ModalDialogLoop.IsActive);
        Assert.False(ModalDialogLoop.RejectReentry());
    }

    // ================================================================
    // 다이얼로그 3종 — 재진입 호출이 살아 있는 상태를 건드리지 않아야 한다
    // ================================================================

    [Fact]
    public void CleanupDialog_재진입_호출은_살아있는_항목목록을_보존한다()
    {
        FieldInfo items = Field(typeof(CleanupDialog), "_items");
        FieldInfo scrollPos = Field(typeof(CleanupDialog), "_scrollPos");

        // 첫 다이얼로그가 사용 중인 상태를 모사 — 스크롤은 0 이 아닌 값이라야 프롤로그의 리셋을 잡는다.
        var live = new List<string> { "notepad.exe", "chrome.exe" };
        items.SetValue(null, live);
        scrollPos.SetValue(null, 42);

        try
        {
            List<string>? result = null;
            WhileModalActive(() => result = CleanupDialog.Show(DummyOwner, ["other.exe"]));

            Assert.Null(result);
            // 가드가 없으면 프롤로그가 새 목록으로 덮고 에필로그가 null 로 만든다.
            Assert.Same(live, items.GetValue(null));
            Assert.Equal(42, scrollPos.GetValue(null));
        }
        finally
        {
            items.SetValue(null, null);
            scrollPos.SetValue(null, 0);
        }
    }

    [Fact]
    public void SettingsDialog_재진입_호출은_편집중인_config_와_콜백을_보존한다()
    {
        FieldInfo working = Field(typeof(SettingsDialog), "_workingConfig");
        FieldInfo callback = Field(typeof(SettingsDialog), "_updateCallback");

        // 첫 다이얼로그가 사용자의 편집을 반영해 들고 있는 작업본.
        var edited = new AppConfig() with { Opacity = 0.37 };
        Action<AppConfig> liveCallback = _ => { };
        working.SetValue(null, edited);
        callback.SetValue(null, liveCallback);

        try
        {
            bool secondCallbackFired = false;
            WhileModalActive(() => SettingsDialog.Show(
                DummyOwner, new AppConfig(),
                currentConfig: static () => new AppConfig(),
                updateConfig: _ => secondCallbackFired = true));

            Assert.False(secondCallbackFired);
            // 가드가 없으면 프롤로그가 두 번째 호출의 스냅샷으로 작업본과 콜백을 통째로 교체한다.
            Assert.Same(edited, working.GetValue(null));
            Assert.Same(liveCallback, callback.GetValue(null));
        }
        finally
        {
            working.SetValue(null, null);
            callback.SetValue(null, null);
        }
    }

    [Fact]
    public void ScaleInputDialog_재진입_호출은_살아있는_입력창과_종료플래그를_보존한다()
    {
        FieldInfo hwndEdit = Field(typeof(ScaleInputDialog), "_hwndScaleEdit");
        FieldInfo parsed = Field(typeof(ScaleInputDialog), "_scaleDlgParsedValue");
        FieldInfo closed = Field(typeof(ScaleInputDialog), "_scaleDlgClosed");

        var liveEdit = (IntPtr)0xABCD;
        hwndEdit.SetValue(null, liveEdit);
        parsed.SetValue(null, 2.5);
        // 첫 다이얼로그가 이미 닫히기로 한 순간 — 프롤로그의 리셋은 그 모달 루프를 되살린다.
        closed.SetValue(null, true);

        try
        {
            double? result = null;
            WhileModalActive(() => result = ScaleInputDialog.Show(DummyOwner, 1.0));

            Assert.Null(result);
            Assert.Equal(liveEdit, hwndEdit.GetValue(null));
            Assert.Equal(2.5, parsed.GetValue(null));
            Assert.True((bool)closed.GetValue(null)!);
        }
        finally
        {
            hwndEdit.SetValue(null, IntPtr.Zero);
            parsed.SetValue(null, 0d);
            closed.SetValue(null, false);
        }
    }
}
