using System.Runtime.InteropServices;
using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.Detector;

/// <summary>
/// IME/포그라운드 감지 스레드 루프. Program 정적 상태는 <see cref="DetectionHost"/> 콜백으로만 읽는다.
/// </summary>
internal static class DetectionService
{
    /// <summary>
    /// 감지 루프의 tick 간 상태. 로컬 변수 9개를 한 구조체로 묶어
    /// 분할된 헬퍼들이 ref 로 공유한다. 스레드 간 공유 상태 아님 (감지 스레드 전용).
    /// </summary>
    private struct DetectionState
    {
        public IntPtr LastHwndFocus;
        public IntPtr LastHwndForeground;
        public string LastForegroundProcessName;
        public RECT LastSystemInputFrame;
        public RECT LastWindowFrame;
        public bool WindowMoving;
        public bool LastFiltered;
        public int FilteredStreak;   // 연속 filtered 폴링 수 — HIDE 디바운스(flip-flop 흡수)용
        public ImeState LastImeState;
        public int PollCount;
    }

    /// <summary>RECT 의 4 필드를 모두 비교해 동등 여부 판정.</summary>
    private static bool RectsEqual(in RECT a, in RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    internal static void RunLoop(DetectionHost host)
    {
        var state = new DetectionState
        {
            LastForegroundProcessName = string.Empty,
            LastImeState = ImeState.English,
        };

        // 연속 실패 누적용 로컬 상태. 지수 백오프로 로그 홍수·CPU 낭비 차단.
        // 동일 메시지 억제를 위한 마지막 오류 텍스트도 함께 추적한다.
        int backoffMs = 0;
        string lastErrorMessage = string.Empty;

        while (!host.IsStopping())
        {
            Thread.Sleep(host.GetConfig().PollIntervalMs + backoffMs);
            try
            {
                ProcessDetectionTick(host, ref state);

                // 성공 — 이전 틱에서 백오프 중이었다면 복구 로그 + 카운터 리셋.
                if (backoffMs > 0)
                {
                    Logger.Info($"Detection loop recovered after backoff (prev={backoffMs}ms)");
                    backoffMs = 0;
                    lastErrorMessage = string.Empty;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or COMException
                or ArgumentException)
            {
                // 감지 루프 본문은 P/Invoke(User32/Dwmapi/Imm32) + VDM COM + Process.GetProcessById
                // 조합이므로 일시적 Win32/COM/프로세스 실패는 흡수하고 다음 폴링에서 재개한다.
                // 로직 버그(NullRef 등)는 전파되어 감지 스레드 종료로 드러난다.
                //
                // 지속 실패 대응: 200ms 씩 가산해 2000ms 까지 backoff. 동일 메시지 재발생은
                // Debug 로 강등해 Warning 홍수 억제. 메시지가 바뀌거나 첫 발생은 Warning 유지.
                backoffMs = Math.Min(backoffMs + DefaultConfig.DetectionBackoffStepMs,
                    DefaultConfig.DetectionBackoffMaxMs);
                if (ex.Message != lastErrorMessage)
                {
                    Logger.Warning($"Detection loop error: {ex.Message} (backoff={backoffMs}ms)");
                    lastErrorMessage = ex.Message;
                }
                else
                {
                    Logger.Debug($"Detection loop error repeats: {ex.Message} (backoff={backoffMs}ms)");
                }
            }
        }
    }

    /// <summary>
    /// 감지 루프의 한 틱. 각 단계가 tick 종료(continue) 를 요청하면 즉시 반환.
    /// </summary>
    private static void ProcessDetectionTick(DetectionHost host, ref DetectionState state)
    {
        // 틱 스냅샷 — 이 틱이 도는 동안 메인 스레드가 _config 를 with 로 교체해도 한 틱 안의 모든
        // 분기가 같은 인스턴스를 참조하도록 시작 시 한 번만 읽는다 (틱 내 일관성). 분할 헬퍼들은
        // 이미 appConfig 스냅샷 또는 DefaultConfig 상수를 쓰므로 직접 _config 를 읽던 곳만 cfg 로 통일.
        AppConfig cfg = host.GetConfig();

        // 세션 잠금 중이고 HideOnLockScreen 활성 상태면 상태 전파 자체를 건너뜀.
        // LastFiltered=true 로 세팅해 잠금 해제 후 첫 정상 틱에서 foregroundChanged 가 유도되도록 한다.
        if (host.IsSessionLocked() && cfg.HideOnLockScreen)
        {
            state.LastFiltered = true;
            return;
        }

        state.PollCount++;

        // 0. config.json 변경 감지 (~5초마다)
        if (state.PollCount % DefaultConfig.ConfigCheckIntervalPolls == 0)
            Settings.CheckConfigFileChange(host.GetHwndMain());

        // 1. 포그라운드 윈도우 확인 — 자기 자신 무시 (메인/오버레이/커서 헤일로 3 hwnd 모두).
        // _hwndCursorOverlay 가드는 cursor 윈도우가 어떤 이유로 foreground 잡혀 SystemFilter 가
        // cursor 클래스 평가하는 race 차단 (안전망).
        IntPtr hwndForeground = User32.GetForegroundWindow();
        if (hwndForeground == host.GetHwndMain()
            || hwndForeground == host.GetHwndOverlay()
            || hwndForeground == host.GetHwndCursorOverlay())
            return;

        // 모달 게이트용 PID 와 GUITHREADINFO 용 threadId 를 한 번에 확보.
        uint threadId = User32.GetWindowThreadProcessId(hwndForeground, out uint fgPid);

        if (TryHandleModalGate(host, ref state, fgPid)) return;

        IntPtr hwndFocus = ResolveFocusWindow(threadId, hwndForeground);

        if (TryHandleFilter(host, ref state, hwndForeground, hwndFocus, cfg, out AppConfig appConfig)) return;

        bool leavingSystemInput = UpdateForegroundProcessCache(ref state, hwndForeground);

        // 필터 해소 또는 포그라운드 변경 → 위치/포커스 갱신
        bool foregroundChanged = (hwndForeground != state.LastHwndForeground) || state.LastFiltered;
        state.LastFiltered = false;

        if (TryHandleSystemInputClose(host, ref state, hwndForeground, hwndFocus, leavingSystemInput))
            return;

        TrackSystemInputFrame(ref state, hwndForeground, ref foregroundChanged);
        TrackWindowMove(host, ref state, hwndForeground, appConfig, ref foregroundChanged);

        EmitStateChanges(host, ref state, hwndForeground, hwndFocus, threadId, appConfig, foregroundChanged);
    }

    /// <summary>
    /// 모달 대화상자 활성 + 포그라운드가 자기 프로세스일 때만 인디를 숨긴다.
    /// Win32 다이얼로그는 소유자 기준 모달일 뿐이라 Alt+Tab 으로 외부 앱에 포커스가
    /// 넘어가면 해당 앱에서는 인디가 정상 표시돼야 한다. PID 비교는 자체 대화상자 +
    /// MessageBoxW (HWND 가 user32 내부 소유라 ActiveDialog HWND 로 식별 불가) 를
    /// 모두 커버하는 유일한 견고 해법 — Environment.ProcessId 는 .NET BCL 속성이라
    /// P/Invoke 불필요. lastFiltered=true 로 모달 종료 후 원 앱 foreground 복귀 첫
    /// 틱에서 foregroundChanged=true 를 유도 → 자연 재표시.
    /// </summary>
    /// <returns>tick 종료 필요 시 true.</returns>
    private static bool TryHandleModalGate(DetectionHost host, ref DetectionState state, uint fgPid)
    {
        if (!ModalDialogLoop.IsActive || fgPid != (uint)Environment.ProcessId)
            return false;

        // PR-26: 레벨 트리거 — LastFiltered 래치와 무관하게 가시 중이면 HIDE.
        // (설정 모달 위 잔류 파생 결함과 동일 뿌리)
        if (host.IsIndicatorVisible())
        {
            if (!state.LastFiltered)
                Logger.Info($"ModalGate triggered HIDE: fgPid={fgPid}, processId={Environment.ProcessId}, ModalActive={ModalDialogLoop.IsActive}");
            else if (Logger.IsEnabled(LogLevel.Debug))
                Logger.Debug($"ModalGate re-HIDE while still visible: fgPid={fgPid}");
            User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_HIDE_INDICATOR,
                IntPtr.Zero, IntPtr.Zero);
        }
        state.LastFiltered = true;
        return true;
    }

    /// <summary>
    /// GUITHREADINFO 로 hwndFocus 를 획득한다. 콘솔 호스트(conhost) 및
    /// UWP 앱 프레임(ApplicationFrameWindow)은 hwndFocus=0 이므로 포그라운드 윈도우로 대체.
    /// </summary>
    internal static IntPtr ResolveFocusWindow(uint threadId, IntPtr hwndForeground)
    {
        GUITHREADINFO gti = default;
        gti.cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>();
        // 실패 시 gti 가 부분만 채워질 수 있으므로 반환값을 체크해 명시적으로 0(no-focus)으로 떨어뜨린다.
        IntPtr hwndFocus = User32.GetGUIThreadInfo(threadId, ref gti) ? gti.hwndFocus : IntPtr.Zero;

        if (hwndFocus == IntPtr.Zero)
        {
            // conhost: 콘솔 호스트는 GUITHREADINFO 에 포커스를 보고하지 않는다.
            // UWP 프레임(ApplicationFrameWindow): 콘텐츠가 별도 프로세스 CoreWindow 라
            //   프레임 스레드 기준 hwndFocus=0 (conhost 와 동형). 둘 다 foreground 를
            //   포커스 타깃으로 대체해 ShouldHide 조건 6(no-focus HIDE) 오탐을 막는다.
            //   단, 시작 메뉴/검색의 Windows.UI.Core.CoreWindow 는 SystemInputProcesses
            //   경로로 정상 숨김되므로 폴백에서 제외 — ApplicationFrameWindow(일반 UWP
            //   앱 프레임)로만 한정한다.
            string fgClass = WindowProcessInfo.GetClassName(hwndForeground);
            if (fgClass.Equals(Win32Constants.ConsoleWindowClass, StringComparison.OrdinalIgnoreCase)
                || fgClass.Equals(Win32Constants.ApplicationFrameWindowClass, StringComparison.OrdinalIgnoreCase))
                hwndFocus = hwndForeground;
        }
        return hwndFocus;
    }

    /// <summary>
    /// 앱별 프로필 + 시스템 필터 평가. 매 폴링 재평가 — 단순함이 정확성을 보장하고,
    /// 같은 앱으로 복귀(데스크톱 → 같은 앱) 시 인디가 다시 보이도록 한다.
    /// 필터링된 경우 숨김 메시지 전송 후 true 반환(continue).
    /// </summary>
    private static bool TryHandleFilter(DetectionHost host, ref DetectionState state, IntPtr hwndForeground, IntPtr hwndFocus,
        AppConfig cfg, out AppConfig appConfig)
    {
        // PR-32 Pointer 축 — FG 히스테리시스와 독립. #32768 등 FG 미변경 메뉴는 즉시 HIDE.
        // Start/Search 는 메인 표시 정책 유지(includeSystemInput: false). 커서만 SystemInput 숨김.
        if (OverlaySuppressProbe.IsPointerOverSuppressSurface(cfg, includeSystemInputProcesses: false))
        {
            if (host.IsIndicatorVisible())
            {
                if (!state.LastFiltered)
                    Logger.Info("Pointer suppress HIDE");
                else if (Logger.IsEnabled(LogLevel.Debug))
                    Logger.Debug("Pointer suppress re-HIDE while still visible");
                User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_HIDE_INDICATOR,
                    IntPtr.Zero, IntPtr.Zero);
            }
            state.LastHwndForeground = hwndForeground;
            state.LastHwndFocus = hwndFocus;
            state.LastFiltered = true;
            state.FilteredStreak = 0; // Pointer 축과 FG 히스테리시스 카운터 분리
            state.LastSystemInputFrame = default;
            appConfig = default!;
            return true;
        }

        AppConfig? resolved = Settings.ResolveForApp(cfg, hwndForeground);
        bool currentlyFiltered = (resolved is null)
            || SystemFilter.ShouldHide(hwndForeground, hwndFocus, resolved);

        if (currentlyFiltered)
        {
            state.FilteredStreak++;

            // flip-flop 디바운스: 일부 창(파일 탐색기 CabinetWClass 등)은 포커스 직후 hwndFocus 가
            // 0↔정상 으로 진동해 매 폴링 filtered↔non-filtered 가 뒤집힌다. 연속 HideHysteresisPolls
            // 회 filtered 일 때만 HIDE 를 확정 — 단발 진동은 흡수해 플로팅 배지 깜박임/사라짐(애니 ON 시
            // FadingOut race 박제)을 막는다. 잠정 구간엔 상태를 갱신하지 않아(LastFiltered/LastHwnd 불변)
            // 현 인디 상태를 유지하고, 다음 틱이 진동의 반대 위상이면 Show 가 자연 복원한다.
            if (state.FilteredStreak < DefaultConfig.HideHysteresisPolls)
            {
                // 감지 스레드 핫패스 — GetClassName(P/Invoke) 가 레벨과 무관하게 매 폴링 평가되던 것을 가드.
                if (Logger.IsEnabled(LogLevel.Debug))
                    Logger.Debug($"Filter HIDE deferred (streak={state.FilteredStreak}/{DefaultConfig.HideHysteresisPolls}, fgClass={WindowProcessInfo.GetClassName(hwndForeground)}, hwndFocus=0x{hwndFocus.ToInt64():X})");
                appConfig = default!;
                return true;
            }

            // 필터 확정(연속 N폴링) — PR-26: 레벨 트리거(_indicatorVisible).
            // LastFiltered 는 중복 억제·foregroundChanged 유도용으로만 유지(PRD §7.2).
            // UserHidden 등 외부 경로로 인디가 다시 켜져도 필터 대상에 머무는 한 HIDE 재전송.
            if (host.IsIndicatorVisible())
            {
                if (!state.LastFiltered)
                    Logger.Info($"Filter triggered HIDE: hwndFg=0x{hwndForeground.ToInt64():X}, hwndFocus=0x{hwndFocus.ToInt64():X}, resolved_null={resolved is null}, fgClass={WindowProcessInfo.GetClassName(hwndForeground)}, streak={state.FilteredStreak}");
                else if (Logger.IsEnabled(LogLevel.Debug))
                    Logger.Debug($"Filter re-HIDE while still visible: hwndFg=0x{hwndForeground.ToInt64():X}, fgClass={WindowProcessInfo.GetClassName(hwndForeground)}");
                User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_HIDE_INDICATOR,
                    IntPtr.Zero, IntPtr.Zero);
            }
            state.LastHwndForeground = hwndForeground;
            state.LastHwndFocus = hwndFocus;
            state.LastFiltered = true;
            state.LastSystemInputFrame = default;
            appConfig = default!;
            return true;
        }

        state.FilteredStreak = 0;
        appConfig = resolved!;
        return false;
    }

    /// <summary>
    /// hwnd 변경 시에만 프로세스 이름 캐시 갱신 (폴링당 Process.GetProcessById 호출 회피).
    /// </summary>
    /// <returns>시스템 입력 프로세스(검색 창 등)에서 일반 앱으로 전환되었는지 — 갱신 전 이름 기준.</returns>
    private static bool UpdateForegroundProcessCache(ref DetectionState state, IntPtr hwndForeground)
    {
        if (hwndForeground == state.LastHwndForeground)
            return false;

        bool leavingSystemInput = DefaultConfig.IsSystemInputProcess(state.LastForegroundProcessName);
        state.LastForegroundProcessName = WindowProcessInfo.GetProcessName(hwndForeground);
        state.LastSystemInputFrame = default;
        // LastWindowFrame 을 default(all-zero) 로 리셋하면 다음 틱의 TrackWindowMove 가
        // 현재 rect 와 0,0,0,0 을 비교해 "창이 이동했다"고 오판정 → 불필요한 hide/show
        // 사이클 + PositionUpdated 로그 중복이 발생. 현재 프레임을 즉시 주입해 첫 비교가
        // 안정 상태(rectChanged=false)로 시작되게 한다.
        state.LastWindowFrame = Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT frame)
            ? frame
            : default;
        state.WindowMoving = false;
        return leavingSystemInput;
    }

    /// <summary>
    /// 시스템 입력 프로세스 닫힘 감지.
    /// 시작 메뉴(StartMenuExperienceHost)와 검색 창(SearchHost)은 SystemFilter 블랙리스트에
    /// 없어 인디케이터를 표시하지만, ESC로 닫힌 뒤에도 숨김 전환이 발생하지 않는 문제가 있다.
    /// 두 프로세스의 ESC 후 동작이 다르므로 두 가지 체크가 필요하다:
    /// <para>
    /// (A) SMEH: ESC 후 foreground를 유지한 채 DWM cloaked 상태가 됨 (수 초간 지속).
    ///     IsWindowVisible=true, hwndFocus≠0이라 ShouldHide 8조건을 모두 통과한다.
    ///     → IsCloaked로 감지하여 숨김.
    /// </para>
    /// <para>
    /// (B) SearchHost: ESC 후 cloaked 없이 foreground가 즉시 다른 앱으로 변경됨.
    ///     non-filtered→non-filtered 전환이라 기존 숨김 로직이 동작하지 않음.
    ///     → leavingSystemInput 플래그로 감지하여 숨김.
    /// </para>
    /// </summary>
    private static bool TryHandleSystemInputClose(DetectionHost host, ref DetectionState state, IntPtr hwndForeground,
        IntPtr hwndFocus, bool leavingSystemInput)
    {
        // (A) HWND 유지 + cloaked: 시작 메뉴 ESC 후 foreground가 아직 안 바뀐 경우
        if (!leavingSystemInput
            && DefaultConfig.IsSystemInputProcess(state.LastForegroundProcessName)
            && Dwmapi.IsCloaked(hwndForeground))
        {
            if (host.IsIndicatorVisible())
            {
                User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_HIDE_INDICATOR,
                    IntPtr.Zero, IntPtr.Zero);
            }
            state.LastHwndForeground = hwndForeground;
            state.LastHwndFocus = hwndFocus;
            state.LastSystemInputFrame = default;
            return true;
        }

        // (B) 즉시 전환: 검색 창 등에서 일반 앱으로 직접 변경된 경우
        //     시스템 입력 간 전환(시작 메뉴 ↔ 검색)은 제외.
        //     인디가 이미 숨겨진 경우(A에 의해)에는 fall-through하여 새 앱에 즉시 표시.
        if (leavingSystemInput
            && !DefaultConfig.IsSystemInputProcess(state.LastForegroundProcessName)
            && host.IsIndicatorVisible())
        {
            User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_HIDE_INDICATOR,
                IntPtr.Zero, IntPtr.Zero);
            // lastHwndForeground 미갱신 → 다음 틱에서 foreground 변경 감지 → 새 앱에 인디 표시
            state.LastHwndFocus = hwndFocus;
            state.LastSystemInputFrame = default;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 시스템 입력 프로세스(시작 메뉴 ↔ 검색 창)는 하나의 HWND를 모드별로 재사용하면서
    /// rect만 바꾸기 때문에 hwnd 비교만으로는 전환을 감지할 수 없다. 같은 hwnd라도
    /// 시각적 프레임이 달라졌다면 포그라운드 변경으로 취급해 위치를 갱신한다.
    /// </summary>
    private static void TrackSystemInputFrame(ref DetectionState state, IntPtr hwndForeground,
        ref bool foregroundChanged)
    {
        if (DefaultConfig.IsSystemInputProcess(state.LastForegroundProcessName)
            && Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT currentFrame)
            && !RectsEqual(currentFrame, state.LastSystemInputFrame))
        {
            foregroundChanged = true;
            state.LastSystemInputFrame = currentFrame;
        }
    }

    /// <summary>
    /// 창 기준 모드: 포그라운드 창 rect 변화 감지 → 이동 중 인디 숨김, 안정화 시 재표시.
    /// 시스템 입력 프로세스는 전용 블록에서 처리하므로 제외.
    /// appConfig 로 틱 스냅샷 일관성 유지 — 같은 틱 안에서 _config 가 교체돼도 경계 조건 안전.
    /// </summary>
    private static void TrackWindowMove(DetectionHost host, ref DetectionState state, IntPtr hwndForeground,
        AppConfig appConfig, ref bool foregroundChanged)
    {
        if (appConfig.PositionMode != PositionMode.Window
            || DefaultConfig.IsSystemInputProcess(state.LastForegroundProcessName)
            || foregroundChanged
            || !Dwmapi.TryGetVisibleFrame(hwndForeground, out RECT windowFrame))
            return;

        bool rectChanged = !RectsEqual(windowFrame, state.LastWindowFrame);

        if (rectChanged)
        {
            if (host.IsIndicatorVisible() && !state.WindowMoving)
                User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_HIDE_INDICATOR,
                    IntPtr.Zero, IntPtr.Zero);
            state.WindowMoving = true;
            state.LastWindowFrame = windowFrame;
        }
        else if (state.WindowMoving)
        {
            // 창 이동 멈춤 → 새 위치에서 인디 재표시
            state.WindowMoving = false;
            foregroundChanged = true;
        }
    }

    /// <summary>
    /// 포그라운드/IME/포커스 변경을 메인 스레드로 PostMessage.
    /// </summary>
    private static void EmitStateChanges(DetectionHost host, ref DetectionState state, IntPtr hwndForeground,
        IntPtr hwndFocus, uint threadId, AppConfig appConfig, bool foregroundChanged)
    {
        // 3. 포그라운드 변경 시 위치 갱신
        if (foregroundChanged)
        {
            User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_POSITION_UPDATED,
                hwndForeground, IntPtr.Zero);
            state.LastHwndForeground = hwndForeground;
        }

        // 4. IME 상태 감지
        ImeState currentIme = ImeStatus.Detect(hwndFocus, threadId, appConfig.DetectionMethod);
        if (currentIme != state.LastImeState || foregroundChanged)
        {
            state.LastImeState = currentIme;
            User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_IME_STATE_CHANGED,
                (IntPtr)(int)currentIme, IntPtr.Zero);
        }

        // 5. 포커스 변경 감지
        if (hwndFocus != state.LastHwndFocus || foregroundChanged)
        {
            state.LastHwndFocus = hwndFocus;
            User32.PostMessageW(host.GetHwndMain(), AppMessages.WM_FOCUS_CHANGED,
                hwndFocus, IntPtr.Zero);
        }
    }
}
