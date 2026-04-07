namespace KoEnVue.Config;

/// <summary>
/// 기본 상수값. 코드 전체에서 매직 넘버 대신 이 상수를 참조한다.
/// config.json에서 오버라이드 가능한 값은 AppConfig 기본값에 정의하고,
/// 여기에는 코드 레벨 픽셀 오프셋/간격/타이밍 상수만 정의한다.
/// </summary>
internal static class DefaultConfig
{
    // === 배치 간격 (px, DPI 스케일링 전 기본값) ===

    /// <summary>캐럿-라벨 간격 (label 스타일)</summary>
    public const int LabelGap = 2;

    /// <summary>포커스 윈도우 fallback 시 윈도우 하단 간격</summary>
    public const int FocusWindowGap = 4;

    /// <summary>caret_dot/square X 오프셋 (캐럿 오른쪽 상단)</summary>
    public const int CaretBoxGapX = 2;

    /// <summary>caret_dot/square Y 오프셋 (캐럿 오른쪽 상단)</summary>
    public const int CaretBoxGapY = 2;

    /// <summary>caret_underline 캐럿 아래 간격</summary>
    public const int UnderlineGap = 1;

    /// <summary>caret_vbar X 오프셋 (캐럿 위치에 겹침)</summary>
    public const int VbarOffsetX = 1;

    /// <summary>label 텍스트 좌우 패딩 (F-S01 고정 너비 계산용)</summary>
    public const int LABEL_PADDING_X = 4;

    // === 애니메이션 타이밍 (ms) ===

    /// <summary>페이드인 지속 시간</summary>
    public const int FadeInDurationMs = 150;

    /// <summary>유지 시간</summary>
    public const int HoldDurationMs = 1500;

    /// <summary>페이드아웃 지속 시간</summary>
    public const int FadeOutDurationMs = 400;

    /// <summary>IME 전환 시 확대 배율</summary>
    public const double ScaleFactor = 1.3;

    /// <summary>확대 -> 원래 크기 복귀 시간</summary>
    public const int ScaleReturnMs = 300;

    // === 감지 ===

    /// <summary>감지 폴링 간격</summary>
    public const int PollingIntervalMs = 80;

    // === DPI ===

    /// <summary>DPI 기준값. DpiHelper.Scale에서 사용.</summary>
    public const int BASE_DPI = 96;

    // === 앱 식별 ===

    /// <summary>
    /// 고정 GUID. 트레이 아이콘 식별 + Mutex 이름에 사용.
    /// 크래시 복구(NIM_DELETE)에서 이전 찌꺼기를 정리하는 데 필수.
    /// </summary>
    public static readonly Guid AppGuid = new("B7E3F2A1-8C4D-4F6E-9A2B-1D5E7F3C8A9B");

    /// <summary>Mutex 이름: "KoEnVue_{GUID}"</summary>
    public static readonly string MutexName = $"KoEnVue_{AppGuid}";

    // === 오버레이 ===

    /// <summary>오버레이 윈도우 클래스명</summary>
    public const string OverlayClassName = "KoEnVueOverlay";

    // === 캐시 ===

    /// <summary>앱별 캐럿 감지 방식 캐시 최대 크기 (LRU)</summary>
    public const int AppMethodCacheMaxSize = 50;

    // === always 모드 ===

    /// <summary>always 모드 유휴 전환 타임아웃</summary>
    public const int AlwaysIdleTimeoutMs = 3000;

    // === 설정 파일 ===

    /// <summary>설정 파일 변경 감지 간격 (약 5초 = 62폴링 x 80ms)</summary>
    public const int ConfigCheckIntervalPolls = 62;

    // === IME 감지 ===

    /// <summary>SendMessageTimeout 타임아웃 (ms)</summary>
    public const uint ImeMessageTimeoutMs = 100;

    // === TOPMOST 재적용 ===

    /// <summary>TOPMOST 재적용 간격 (다른 TOPMOST 앱과 충돌 시)</summary>
    public const int ForceTopmostIntervalMs = 5000;

    // === UIA ===

    /// <summary>UI Automation 호출 타임아웃</summary>
    public const int UiaTimeoutMs = 200;
}
