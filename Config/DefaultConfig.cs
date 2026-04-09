using System;
using System.IO;

namespace KoEnVue.Config;

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

    // === always 모드 ===

    /// <summary>always 모드 유휴 전환 타임아웃</summary>
    public const int AlwaysIdleTimeoutMs = 3000;

    // === 설정 파일 ===

    /// <summary>설정 파일명</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>%APPDATA% 하위 폴더명</summary>
    public const string AppDataFolderName = "KoEnVue";

    /// <summary>기본 설정 파일 경로 (%APPDATA%\KoEnVue\config.json)</summary>
    public static string GetDefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataFolderName, ConfigFileName);

    /// <summary>설정 파일 변경 감지 간격 (약 5초 = 62폴링 x 80ms)</summary>
    public const int ConfigCheckIntervalPolls = 62;

    // === IME 감지 ===

    /// <summary>SendMessageTimeout 타임아웃 (ms)</summary>
    public const uint ImeMessageTimeoutMs = 100;

    // === TOPMOST 재적용 ===

    /// <summary>TOPMOST 재적용 간격 (다른 TOPMOST 앱과 충돌 시)</summary>
    public const int ForceTopmostIntervalMs = 5000;

}
