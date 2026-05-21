namespace KoEnVue.Core.Logging;

/// <summary>
/// Core 레이어의 단일 로깅 진입점. <see cref="Sink"/> 가 set 되기 전 호출은 모두 no-op
/// (null-conditional 접근으로 NullReferenceException 회피).
///
/// <para>
/// 배선 시점: App 레이어의 부트 진입 함수(<c>Program.MainImpl</c>) 의 첫 라인에서
/// <c>LogProvider.Sink = new LoggerSink();</c> 을 호출. 이 라인은 <see cref="Logger.Initialize"/>
/// 보다 먼저 실행되지만 Sink → Logger.X 경로의 pre-Initialize 버퍼 (Logger 내부) 가
/// 메시지를 보관했다가 Initialize 직후 flush 한다.
/// </para>
///
/// <para>
/// Logger 의 Shutdown 직후에는 호출자가 <see cref="Sink"/> 를 null 로 되돌릴 수 있다 — 본 PR 의
/// 일반 종료 경로에선 process exit 가 곧바로 따라오므로 명시적 null 화는 생략. 테스트에서 Sink
/// 교체가 필요하면 set-only 가 아니라 그냥 재대입.
/// </para>
/// </summary>
internal static class LogProvider
{
    public static ILogSink? Sink { get; set; }
}
