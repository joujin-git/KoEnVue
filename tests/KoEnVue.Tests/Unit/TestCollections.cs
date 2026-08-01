using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="KoEnVue.App.UI.Dialogs.SettingsDialog"/> 의 정적 모달 상태를 만지는 테스트들의 공용 컬렉션.
///
/// <para>
/// xUnit 은 서로 다른 테스트 클래스를 기본적으로 <b>병렬</b> 실행한다. 다이얼로그의 모달 상태는
/// 프로세스 전역 static 이라, 한 클래스가 센티넬을 심어 보존을 확인하는 사이 다른 클래스가 같은
/// 필드를 정리하면 서로를 깨뜨린다 — 실제로 <c>ConfigCommitBaseTests</c> 추가 직후
/// <c>ModalReentryGuardTests</c> 가 이 이유로 실패했다. 같은 컬렉션에 넣어 직렬화한다.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class SettingsDialogStaticsCollection
{
    public const string Name = "SettingsDialogStatics";
}
