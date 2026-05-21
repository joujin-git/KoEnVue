namespace KoEnVue.Core.Logging;

/// <summary>
/// Core 레이어가 의존하는 로깅 추상화. <see cref="Logger"/> 의 정적 함수에 직접 결합하지 않도록
/// Core 코드는 본 인터페이스를 통해서만 로그를 남긴다.
///
/// <para>
/// 호출 사이트는 <see cref="LogProvider.Sink"/> 의 null-conditional (<c>?.</c>) 접근을 사용해
/// Sink 미설정 상태에서도 안전하게 no-op 한다 — 부트 초반(LogProvider.Sink 배선 이전) 호출이나
/// 테스트 환경에서 단독으로 Core 코드를 lift-out 했을 때 모두 동일하게 동작한다.
/// </para>
///
/// <para>
/// 레벨 4종(<c>Debug/Info/Warning/Error</c>) 은 <see cref="LogLevel"/> 과 1:1. 메서드 시그너처는
/// 모두 <c>string</c> 단일 인자로 — 보간 문자열 합성은 호출 사이트가 책임진다.
/// </para>
/// </summary>
internal interface ILogSink
{
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
