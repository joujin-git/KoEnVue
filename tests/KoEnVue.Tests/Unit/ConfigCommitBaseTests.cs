using System.Reflection;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.App.UI.Dialogs;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// 설정 커밋의 <b>베이스</b>가 "지금 값" 인지 고정 (AUDIT-2026-07-30 §B).
///
/// <para>
/// 트레이 메뉴와 상세 설정 다이얼로그는 둘 다 "열릴 때의 config 스냅샷" 을 받아 그 위에
/// <c>with { … }</c> 를 얹어 커밋했다. 그런데 둘 다 <b>자체 모달 루프</b> 안에서 산다 —
/// <c>TrackPopupMenu</c> 와 <c>ModalDialogLoop</c> 모두 메시지를 계속 디스패치하므로, 그 사이
/// 감지 스레드가 post 한 <c>WM_CONFIG_CHANGED</c> 가 처리돼 <c>_config</c> 가 교체될 수 있다.
/// 그러면 커밋이 <b>옛 베이스</b> 위에 얹혀 방금 반영된 외부 편집을 조용히 되돌리고, 뒤따르는
/// <c>Settings.Save</c> 가 디스크까지 덮었다.
/// </para>
///
/// <para>
/// 그래서 검사하는 것은 "사용자가 바꾼 값이 반영됐는가"(그건 원래 됐다)가 아니라 <b>사용자가
/// 건드리지 않은 필드가 살아남는가</b>이다. 스냅샷 베이스로 되돌아가면 그 필드가 옛 값이 된다.
/// </para>
/// </summary>
[Collection(SettingsDialogStaticsCollection.Name)]
public class ConfigCommitBaseTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>메뉴 명령 ID 는 private const — P3 상 테스트에 리터럴을 박지 않는다.</summary>
    private static int MenuId(string name) =>
        (int)typeof(Tray).GetField(name, PrivateStatic)!.GetRawConstantValue()!;

    // ================================================================
    // 트레이 메뉴 — 메뉴가 열려 있는 동안의 외부 편집
    // ================================================================

    [Fact]
    public void 메뉴명령은_스냅샷이_아니라_현재_config_위에_얹힌다()
    {
        // 메뉴를 띄울 때의 스냅샷.
        var snapshot = new AppConfig() with { SnapToWindows = false, Opacity = 0.9 };
        // 메뉴가 열려 있는 동안 koenvue_config.json 이 외부에서 편집돼 핫리로드된 상태.
        var current = new AppConfig() with { SnapToWindows = false, Opacity = 0.25 };

        AppConfig? committed = null;
        Tray.HandleMenuCommand(
            MenuId("IDM_SNAP_TO_WINDOWS"), snapshot, IntPtr.Zero, IntPtr.Zero,
            currentConfig: () => current,
            updateConfig: c => committed = c);

        Assert.NotNull(committed);
        Assert.True(committed!.SnapToWindows);              // 사용자가 누른 토글은 반영
        Assert.Equal(0.25, committed.Opacity, precision: 6); // 외부 편집 보존 (스냅샷 베이스면 0.9)
    }

    // ================================================================
    // 상세 설정 다이얼로그 — 다이얼로그가 떠 있는 동안의 외부 편집
    // ================================================================

    [Fact]
    public void 다이얼로그_커밋_베이스는_현재_config_다()
    {
        FieldInfo initial = typeof(SettingsDialog).GetField("_initialConfig", PrivateStatic)!;
        FieldInfo working = typeof(SettingsDialog).GetField("_workingConfig", PrivateStatic)!;
        FieldInfo provider = typeof(SettingsDialog).GetField("_currentConfigProvider", PrivateStatic)!;
        FieldInfo fields = typeof(SettingsDialog).GetField("_fields", PrivateStatic)!;
        MethodInfo tryCommit = typeof(SettingsDialog).GetMethod("TryCommit", PrivateStatic)!;

        var snapshot = new AppConfig() with { Opacity = 0.9 };
        var current = new AppConfig() with { Opacity = 0.25 };

        // 필드 목록이 비어 있으면 TryCommit 은 베이스를 그대로 _workingConfig 로 확정한다 —
        // 컨트롤 HWND 없이 베이스 선택만 정확히 관측할 수 있는 구성.
        ((System.Collections.IList)fields.GetValue(null)!).Clear();
        initial.SetValue(null, snapshot);
        working.SetValue(null, snapshot);
        provider.SetValue(null, (Func<AppConfig>)(() => current));

        try
        {
            Assert.True((bool)tryCommit.Invoke(null, null)!);

            var result = (AppConfig)working.GetValue(null)!;
            // _initialConfig 를 베이스로 삼던 시절엔 0.9 가 나오고, 그 값이 그대로 디스크에 저장됐다.
            Assert.Equal(0.25, result.Opacity, precision: 6);
        }
        finally
        {
            provider.SetValue(null, null);
            initial.SetValue(null, null);
            working.SetValue(null, null);
        }
    }

    [Fact]
    public void 공급자가_없으면_스냅샷_베이스로_폴백한다()
    {
        // SettingsDialog.Show 를 거치지 않는 직접 호출 방어 — 폴백이 없으면 NRE 가 된다.
        FieldInfo initial = typeof(SettingsDialog).GetField("_initialConfig", PrivateStatic)!;
        FieldInfo working = typeof(SettingsDialog).GetField("_workingConfig", PrivateStatic)!;
        FieldInfo provider = typeof(SettingsDialog).GetField("_currentConfigProvider", PrivateStatic)!;
        FieldInfo fields = typeof(SettingsDialog).GetField("_fields", PrivateStatic)!;
        MethodInfo tryCommit = typeof(SettingsDialog).GetMethod("TryCommit", PrivateStatic)!;

        var snapshot = new AppConfig() with { Opacity = 0.77 };

        ((System.Collections.IList)fields.GetValue(null)!).Clear();
        initial.SetValue(null, snapshot);
        working.SetValue(null, snapshot);
        provider.SetValue(null, null);

        try
        {
            Assert.True((bool)tryCommit.Invoke(null, null)!);
            Assert.Equal(0.77, ((AppConfig)working.GetValue(null)!).Opacity, precision: 6);
        }
        finally
        {
            initial.SetValue(null, null);
            working.SetValue(null, null);
        }
    }
}
