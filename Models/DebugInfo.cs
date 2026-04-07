namespace KoEnVue.Models;

/// <summary>
/// 디버그 오버레이 표시용 불변 데이터.
/// 감지 스레드 → 메인 스레드 전달 (volatile 참조 교체).
/// </summary>
internal sealed record DebugInfo(
    string Method,
    int CaretX,
    int CaretY,
    uint DpiX,
    long PollingMs,
    string ClassName);
