using System;
using System.IO;

namespace KoEnVue.App.Config;

/// <summary>
/// 기본 상수값. 코드 전체에서 매직 넘버 대신 이 상수를 참조한다.
/// config.json에서 오버라이드 가능한 값은 AppConfig 기본값에 정의하고,
/// 여기에는 코드 레벨 픽셀 오프셋/간격/타이밍 상수만 정의한다.
/// </summary>
internal static class DefaultConfig
{
    // === 배치 (px, DPI 스케일링 전 기본값) ===

    /// <summary>label 텍스트 좌우 패딩</summary>
    public const int LABEL_PADDING_X = 4;

    /// <summary>
    /// 저장 위치가 없는 앱의 기본 인디케이터 위치 — work area TopRight 모서리 기준 X 오프셋.
    /// 음수 = 모서리에서 왼쪽으로. AppConfig.DefaultIndicatorPosition이 null일 때 폴백.
    /// </summary>
    public const int DefaultIndicatorOffsetX = -200;

    /// <summary>
    /// 저장 위치가 없는 앱의 기본 인디케이터 위치 — work area Top 모서리 기준 Y 오프셋.
    /// 양수 = 모서리에서 아래로. AppConfig.DefaultIndicatorPosition이 null일 때 폴백.
    /// </summary>
    public const int DefaultIndicatorOffsetY = 10;

    /// <summary>
    /// 드래그 중 창 엣지 스냅 임계값 (DPI 스케일링 전 px).
    /// 인디케이터 엣지와 타겟 엣지의 거리가 이 값 이하면 스냅.
    /// </summary>
    public const int SnapThresholdPx = 10;

    /// <summary>
    /// 스냅 후보 창의 최소 크기 필터 (DPI 스케일링 전 px). 너무 작은 창(툴팁, 팝업 등) 제외.
    /// </summary>
    public const int SnapMinWindowSizePx = 80;

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

    /// <summary>애니메이션 프레임 간격 (~60fps)</summary>
    public const uint AnimationFrameMs = 16;

    /// <summary>CAPS LOCK 폴링 간격 (메인 스레드 WM_TIMER 주기)</summary>
    public const uint CapsLockPollMs = 200;

    /// <summary>Dim 모드 투명도 감소 계수 (50%)</summary>
    public const double DimOpacityFactor = 0.5;

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

    /// <summary>
    /// 시스템 입력 프로세스 — 시작 메뉴, 작업 표시줄 검색 창.
    /// 기본 위치를 포그라운드 창 중앙 상단으로 보정하여 가시성을 확보한다.
    /// 프로세스명 (확장자 없음, 대소문자 무관).
    /// </summary>
    public static readonly string[] SystemInputProcesses =
    [
        "StartMenuExperienceHost",
        "SearchHost",
        "SearchApp",
    ];

    /// <summary>
    /// 시스템 입력 프로세스 여부. 위치 저장/복원 시 우회하기 위해 사용.
    /// 사용자가 인디를 시스템 창 위로 드래그해도 z-band 한계로 가려지므로
    /// 저장된 위치 대신 항상 기본 위치(창 중앙 상단)를 사용해야 한다.
    /// </summary>
    public static bool IsSystemInputProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        foreach (string p in SystemInputProcesses)
        {
            if (p.Equals(processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // === always 모드 ===

    /// <summary>always 모드 유휴 전환 타임아웃</summary>
    public const int AlwaysIdleTimeoutMs = 3000;

    // === 설정 파일 ===

    /// <summary>설정 파일명 (exe 디렉토리에 생성됨 — 완전 포터블).</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>설정 파일 변경 감지 간격 (약 5초 = 62폴링 x 80ms)</summary>
    public const int ConfigCheckIntervalPolls = 62;

    // === IME 감지 ===

    /// <summary>SendMessageTimeout 타임아웃 (ms)</summary>
    public const uint ImeMessageTimeoutMs = 100;

    // === TOPMOST 재적용 ===

    /// <summary>TOPMOST 재적용 간격 (다른 TOPMOST 앱과 충돌 시)</summary>
    public const int ForceTopmostIntervalMs = 5000;

}
