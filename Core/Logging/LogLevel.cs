namespace KoEnVue.Core.Logging;

/// <summary>
/// 로그 출력 레벨. <c>config.json</c> 의 <c>"log_level"</c> 키에 대응.
///
/// <para>
/// 본 Core enum 은 STJ 의존이 없다 — JSON 표현(<c>"DEBUG"/"INFO"/"WARNING"/"ERROR"</c>) 매핑은 App
/// 레이어의 <c>LogLevelJsonConverter</c> (또는 동등 attribute) 가 담당한다. Core 는 P1 (Zero NuGet)
/// 뿐 아니라 STJ 마저도 의존하지 않는 순수 enum 으로 lift-out 가능하다.
/// </para>
/// </summary>
internal enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}
