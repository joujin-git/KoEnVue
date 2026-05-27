using System;
using KoEnVue.App.Models;

namespace KoEnVue.App.Config;

/// <summary>
/// 기본 상수값. 코드 전체에서 매직 넘버 대신 이 상수를 참조한다.
/// config.json에서 오버라이드 가능한 값은 AppConfig 기본값에 정의하고,
/// 여기에는 코드 레벨 픽셀 오프셋/간격/타이밍 상수만 정의한다.
///
/// <para>
/// PR-11 D6: <c>partial</c> — <c>AppVersion</c> const 는 [Directory.Build.targets](../../Directory.Build.targets)
/// 의 <c>GenerateVersionConstants</c> Target 가 <c>obj/.../Version.g.cs</c> 로 emit 한
/// partial 조각에 정의된다. KoEnVue.csproj 의 <c>&lt;Version&gt;</c> 가 단일 진실원.
/// </para>
/// </summary>
internal static partial class DefaultConfig
{
    // === 배치 (px, DPI 스케일링 전 기본값) ===

    /// <summary>label 텍스트 좌우 패딩</summary>
    public const int LABEL_PADDING_X = 4;

    /// <summary>
    /// 모달 다이얼로그 (Settings / Cleanup / ScaleInput) 의 9pt 시스템 폰트 패밀리.
    /// <c>Core</c> 레이어 (<c>Win32DialogHelper</c> / <c>DialogShell</c>) 는 한국어 폰트
    /// 어휘를 알지 않도록 P6 게이트를 지키기 위해 본 상수가 App 측 단일 진실원이 된다.
    /// </summary>
    public const string DefaultDialogFontFamily = "맑은 고딕";

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
    /// 창 기준 모드 기본 앵커 코너.
    /// AppConfig.DefaultIndicatorPositionRelative가 null일 때 폴백.
    /// </summary>
    public const Corner DefaultRelativeCorner = Corner.TopRight;

    /// <summary>
    /// 창 기준 모드 기본 위치 — 창의 앵커 코너 기준 X 오프셋.
    /// 음수 = 모서리에서 왼쪽으로.
    /// </summary>
    public const int DefaultRelativeOffsetX = -50;

    /// <summary>
    /// 창 기준 모드 기본 위치 — 창의 앵커 코너 기준 Y 오프셋.
    /// 양수 = 모서리에서 아래로.
    /// </summary>
    public const int DefaultRelativeOffsetY = 10;

    /// <summary>
    /// 드래그 중 창 엣지 스냅 임계값 (DPI 스케일링 전 px).
    /// 인디케이터 엣지와 타겟 엣지의 거리가 이 값 이하면 스냅.
    /// </summary>
    public const int SnapThresholdPx = 10;

    // === 애니메이션 타이밍 (ms) ===
    // 이름은 AppConfig 동등 필드와 일치시킨다 (N3) — AppConfig 의 init 디폴트에서 이 상수를
    // 참조하므로 한 곳에서 값을 변경하면 양쪽이 자동으로 동기화된다.

    /// <summary>페이드인 지속 시간 — <see cref="AppConfig.FadeInMs"/> 의 디폴트.</summary>
    public const int FadeInMs = 150;

    /// <summary>유지 시간</summary>
    public const int HoldDurationMs = 1500;

    /// <summary>페이드아웃 지속 시간 — <see cref="AppConfig.FadeOutMs"/> 의 디폴트.</summary>
    public const int FadeOutMs = 400;

    /// <summary>IME 전환 시 확대 배율 — <see cref="AppConfig.HighlightScale"/> 의 디폴트.</summary>
    public const double HighlightScale = 1.3;

    /// <summary>확대 -> 원래 크기 복귀 시간 — <see cref="AppConfig.HighlightDurationMs"/> 의 디폴트.</summary>
    public const int HighlightDurationMs = 300;

    /// <summary>애니메이션 프레임 간격 (~60fps)</summary>
    public const uint AnimationFrameMs = 16;

    /// <summary>CAPS LOCK 폴링 간격 (메인 스레드 WM_TIMER 주기)</summary>
    public const uint CapsLockPollMs = 200;

    /// <summary>Dim 모드 투명도 감소 계수 (50%)</summary>
    public const double DimOpacityFactor = 0.5;

    // === 감지 ===

    /// <summary>감지 폴링 간격 — <see cref="AppConfig.PollIntervalMs"/> 의 디폴트.</summary>
    public const int PollIntervalMs = 80;

    /// <summary>
    /// 감지 루프 지수 백오프 가산치 — 연속 실패 시 tick 간격에 누적으로 더한다.
    /// 예: 실패 3회 후 Thread.Sleep(_config.PollIntervalMs + 600ms).
    /// </summary>
    public const int DetectionBackoffStepMs = 200;

    /// <summary>
    /// 감지 루프 지수 백오프 상한 — tick 간격 + backoff 합이 이 값을 넘지 않는다.
    /// 종료 신호(<c>_stopping</c>) 응답성을 위해 2초로 캡.
    /// </summary>
    public const int DetectionBackoffMaxMs = 2000;

    // === 앱 식별 ===

    /// <summary>
    /// 고정 GUID. 트레이 아이콘 식별 + Mutex 이름에 사용.
    /// 크래시 복구(NIM_DELETE)에서 이전 찌꺼기를 정리하는 데 필수.
    /// </summary>
    public static readonly Guid AppGuid = new("B7E3F2A1-8C4D-4F6E-9A2B-1D5E7F3C8A9B");

    /// <summary>Mutex 이름: "KoEnVue_{GUID}"</summary>
    public static readonly string MutexName = $"KoEnVue_{AppGuid}";

    // AppVersion 은 Directory.Build.targets / GenerateVersionConstants Target 가 자동 생성하는
    // obj/.../Version.g.cs (partial DefaultConfig) 에 박힌다. csproj <Version> 만 바꾸면
    // PE 헤더 (AssemblyVersion/FileVersion/InformationalVersion 3종) 와 본 const 가 한 번에 정합.

    /// <summary>UpdateChecker 가 조회할 GitHub 레포 owner.</summary>
    public const string UpdateRepoOwner = "joujin-git";

    /// <summary>UpdateChecker 가 조회할 GitHub 레포 이름.</summary>
    public const string UpdateRepoName = "KoEnVue";

    // === 시스템 입력 프로세스 ===

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

    /// <summary>always 모드 유휴 전환 타임아웃 — <see cref="AppConfig.AlwaysIdleTimeoutMs"/> 의 디폴트.</summary>
    public const int AlwaysIdleTimeoutMs = 3000;

    // === Validate clamp / SettingsDialog field range — Min/Max 단일 진실원 (D7) ===
    // Settings.Validate 의 Math.Clamp 인자와 SettingsDialog.Fields.cs 의 min/max 인자를 모두 본 const
    // 참조로 통일한다. 두 곳에 같은 리터럴을 두면 한 쪽만 변경됐을 때 다이얼로그 입력 → Validate 클램프
    // 의 silent 보정 가능성이 생긴다.

    public const int    MinPollMs                = 50;
    public const int    MaxPollMs                = 500;
    public const int    MinEventDisplayMs        = 500;
    public const int    MaxEventDisplayMs        = 10000;
    public const int    MinAlwaysIdleMs          = 1000;
    public const int    MaxAlwaysIdleMs          = 30000;
    public const double MinOpacity               = 0.1;
    public const double MaxOpacity               = 1.0;
    public const double MinHighlightScale        = 1.0;
    public const double MaxHighlightScale        = 2.0;
    public const int    MinFadeMs                = 0;
    public const int    MaxFadeMs                = 2000;
    public const int    MinSnapGapPx             = 0;
    public const int    MaxSnapGapPx             = 10;
    public const int    MinFontSize              = 8;
    public const int    MaxFontSize              = 36;
    public const int    MinLabelWidth            = 16;
    public const int    MaxLabelWidth            = 128;
    public const int    MinLabelHeight           = 12;
    public const int    MaxLabelHeight           = 96;
    public const int    MinLabelBorderRadius     = 0;
    public const int    MaxLabelBorderRadius     = 48;
    public const int    MinBorderWidth           = 0;
    public const int    MaxBorderWidth           = 8;
    public const double MinIndicatorScale        = 1.0;
    public const double MaxIndicatorScale        = 5.0;
    public const int    MinLogMaxSizeMb          = 1;
    public const int    MaxLogMaxSizeMb          = 100;
    public const int    MinForceTopmostMs        = 0;
    public const int    MaxForceTopmostMs        = 60000;

    // === 커서 인디케이터 (D7 — Settings.Validate clamp + SettingsDialog field range 단일 진실원) ===
    // AppConfig 의 init 디폴트가 이 const 를 참조하므로 한 곳에서 값을 변경하면 양쪽이 자동 동기화.

    public const bool   CursorIndicatorEnabled     = false;
    public const bool   CursorAlwaysShow           = false;
    public const int    CursorOuterRadius          = 45;
    public const int    CursorMiddleRadius         = 30;
    public const int    CursorInnerRadius          = 15;
    public const int    CursorCoreThickness        = 2;
    public const int    CursorHaloThickness        = 3;
    public const double CursorHaloOpacity          = 0.5;
    public const int    CursorIdleDelayMs          = 100;
    public const int    CursorMotionThresholdPx    = 5;

    public const int    MinCursorOuterRadius       = 8;
    public const int    MaxCursorOuterRadius       = 96;
    public const int    MinCursorMiddleRadius      = 6;
    public const int    MaxCursorMiddleRadius      = 80;
    public const int    MinCursorInnerRadius       = 4;
    public const int    MaxCursorInnerRadius       = 64;
    public const int    MinCursorCoreThickness     = 1;
    public const int    MaxCursorCoreThickness     = 8;
    public const int    MinCursorHaloThickness     = 0;
    public const int    MaxCursorHaloThickness     = 12;
    public const double MinCursorHaloOpacity       = 0.0;
    public const double MaxCursorHaloOpacity       = 1.0;
    public const int    MinCursorIdleDelayMs       = 0;
    public const int    MaxCursorIdleDelayMs       = 2000;
    public const int    MinCursorMotionThresholdPx = 1;
    public const int    MaxCursorMotionThresholdPx = 32;

    /// <summary>cursor 인디 마우스 모션 폴링 주기 (정지 검출 모드 — 50ms).</summary>
    public const uint   CursorMotionPollMs         = 50;

    /// <summary>cursor 인디 마우스 모션 폴링 주기 (항상 표시 모드 — 16ms ≈ 60fps 추종).</summary>
    public const uint   CursorAlwaysPollMs         = 16;

    /// <summary>cursor 인디 페이드 인 지속 시간 (정지 진입 시).</summary>
    public const int    CursorFadeInMs             = 150;

    /// <summary>cursor 인디 페이드 아웃 지속 시간 (이동 진입 시).</summary>
    public const int    CursorFadeOutMs            = 100;

    // === 설정 파일 ===

    /// <summary>설정 파일명 (exe 디렉토리에 생성됨 — 완전 포터블).</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>설정 파일 변경 감지 간격 (약 5초 = 62폴링 x 80ms)</summary>
    public const int ConfigCheckIntervalPolls = 62;

    // === IME 감지 ===

    /// <summary>SendMessageTimeout 타임아웃 (ms)</summary>
    public const uint ImeMessageTimeoutMs = 100;

    // === 시스템 권한 (UIPI / admin 콘솔 IME) ===

    /// <summary>
    /// admin 권한 자동 elevation 기본값. false = asInvoker 그대로 (PR-03 정책 보존).
    /// true 시 자체 elevation (단일 실행, UAC 1회) + schtasks /RL HIGHEST (부팅 자동, UAC 0)
    /// 분담으로 UIPI 차단 우회. <see cref="AppConfig.AdminElevation"/> 의 디폴트.
    /// 상세: docs/improvement-plan/PR-15-admin-elevation.md
    /// </summary>
    public const bool AdminElevation = false;

}
