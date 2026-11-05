using KoEnVue.Core.Native;

namespace KoEnVue.App.Messaging;

/// <summary>
/// 커스텀 윈도우 메시지 정의.
/// 감지 스레드와 훅 콜백이 PostMessage로 메인 스레드에 이벤트를 전달할 때 사용.
/// UI 레이어 밖(<c>App/Messaging</c>)에 두어 Detector/Config → UI 역방향 의존을 피한다.
/// </summary>
internal static class AppMessages
{
    // --- WM_APP 기반 커스텀 메시지 ---

    /// <summary>
    /// IME 상태 변경.
    /// wParam: (IntPtr)(int)ImeState enum 값
    /// lParam: 샘플 시각(<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>, 0 = 모름) — 감지 루프와 WinEvent 훅이
    ///         각자 보내므로 수신부가 더 이른 샘플을 버리는 데 쓴다 (bug-hunt 2026-11-05).
    /// </summary>
    public const uint WM_IME_STATE_CHANGED = Win32Constants.WM_APP + 1;

    /// <summary>
    /// 포커스 윈도우 변경.
    /// wParam: 새 hwndFocus (IntPtr)
    /// lParam: 0
    /// </summary>
    public const uint WM_FOCUS_CHANGED = Win32Constants.WM_APP + 2;

    /// <summary>
    /// 포그라운드 윈도우 위치 갱신 (타이틀바 배치용).
    /// wParam: hwndForeground (IntPtr)
    /// lParam: 0
    /// </summary>
    public const uint WM_POSITION_UPDATED = Win32Constants.WM_APP + 3;

    /// <summary>
    /// 한/영 배지 즉시 숨기기.
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_HIDE_INDICATOR = Win32Constants.WM_APP + 4;

    /// <summary>
    /// 설정 변경 감지 (koenvue_config.json 리로드 또는 트레이 메뉴 변경).
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_CONFIG_CHANGED = Win32Constants.WM_APP + 5;

    /// <summary>
    /// UpdateChecker 백그라운드 스레드가 새 버전을 발견했을 때 메인 스레드로 통지.
    /// 페이로드(<see cref="Update.UpdateInfo"/>)는 Program 내부 volatile 필드로 전달.
    /// wParam: 0
    /// lParam: 0
    /// </summary>
    public const uint WM_APP_UPDATE_FOUND = Win32Constants.WM_APP + 6;

    /// <summary>
    /// 중복 실행된 두 번째 인스턴스가 기존(실행 중) 인스턴스에 활성화를 요청 — <c>SendMessageTimeoutW</c> 로 보낸다.
    /// 수신 시 현재 포그라운드 앱 기준으로 한/영 배지를 즉시 표시해 사용자에게
    /// "이미 실행 중" 이라는 시각 피드백을 준다.
    /// wParam: 0
    /// lParam: 0
    /// 반환: 살아 있는 인스턴스면 <see cref="ActivateAck"/>, 종료 중이면 0.
    /// </summary>
    public const uint WM_APP_ACTIVATE = Win32Constants.WM_APP + 7;

    /// <summary>
    /// <see cref="WM_APP_ACTIVATE"/> 에 살아 있는 기존 인스턴스가 돌려주는 값. 종료 중이거나 응답이 없으면 0 이다 —
    /// 두 번째 인스턴스는 이때 뮤텍스를 기다렸다 이어받는다 (bug-hunt 2026-11-05).
    /// </summary>
    public const nint ActivateAck = 1;

    /// <summary>
    /// 우리 트레이 메뉴 위 포인터 때문에 숨겼던 배지를 되돌린다 — 감지 스레드가 보낸다(<c>DetectionService.HandleOwnMainForeground</c>).
    /// 메뉴가 열린 동안 포그라운드는 보이지 않는 메인 창이라, 다른 앱 메뉴처럼 "필터 통과 → 재표시" 로 돌아오지 못한다. 그래서
    /// 숨길 때 배지가 보이고 있었던 경우에만, 포인터가 메뉴를 벗어나면(메뉴가 닫힌 경우 포함) 이 메시지로 되돌린다 — 그 배지는
    /// 직전까지 배지 주인 앱 위에 보이던 것이므로 필터를 다시 판정하지 않는다 (릴리즈 리뷰 2026-11-05).
    /// wParam: 0 / lParam: 0
    /// </summary>
    public const uint WM_RESTORE_INDICATOR = Win32Constants.WM_APP + 8;

    // --- Timer IDs (WM_TIMER wParam) ---

    // 프레임 타이머 주기 = DefaultConfig.AnimationFrameMs (15 → SetTimer 양자화로 실배달 ~15.6ms).
    // 16 으로 적지 말 것 — uElapse=16 은 ~31ms 로 배달된다 (dev-notes/2026-07-22-settimer-tick-quantization.md).
    public const nuint TIMER_ID_FADE          = 1;  // AnimationFrameMs, 페이드 애니메이션
    public const nuint TIMER_ID_HOLD          = 2;  // one-shot, 유지 시간
    public const nuint TIMER_ID_HIGHLIGHT     = 3;  // AnimationFrameMs, 강조 스케일
    public const nuint TIMER_ID_TOPMOST       = 4;  // 5000ms, TOPMOST 재적용
    public const nuint TIMER_ID_SLIDE         = 5;  // AnimationFrameMs, 슬라이드 위치 보간
    public const nuint TIMER_ID_CAPS          = 6;  // 200ms, CAPS LOCK 토글 폴링 (메인 스레드)
    public const nuint TIMER_ID_TRAY_ADD_RETRY = 7; // 1000ms, NIM_ADD 재시도 (부팅 레이스 복구)
    public const nuint TIMER_ID_CURSOR_MOTION  = 8; // CursorMotionPollMs (정지 검출) 또는 CursorAlwaysPollMs (항상 표시 모드, 실배달 ~15.6ms), 커서 헤일로 마우스 모션 폴링
    public const nuint TIMER_ID_CURSOR_POP     = 9; // AnimationFrameMs, 커서 헤일로 IME 전환 스케일 팝

    // --- WM_USER 기반 ---

    /// <summary>
    /// 트레이 아이콘 콜백.
    /// Shell_NotifyIconW의 uCallbackMessage에 설정.
    /// </summary>
    public const uint WM_TRAY_CALLBACK = Win32Constants.WM_USER + 1;
}
