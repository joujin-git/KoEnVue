using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.Core.Native;
using KoEnVue.Core.Color;
using KoEnVue.Core.Dpi;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Windowing;
using KoEnVue.App.Localization;

namespace KoEnVue.App.Detector;

/// <summary>
/// IME 한/영 상태 감지. 3-tier fallback + SetWinEventHook 하이브리드.
/// </summary>
internal static class ImeStatus
{
    // 메인 스레드 전용. OnImeChange (WINEVENT_OUTOFCONTEXT 콜백) 만이 읽고 쓰며,
    // 훅은 RegisterHook 을 호출한 스레드(=메인)의 메시지 루프에서 발화하므로 동기화 불필요.
    // 감지 스레드의 Detect() 는 _lastState 를 건드리지 않는다.
    private static ImeState _lastState = ImeState.English;
    private static IntPtr _hEventHook;
    private static IntPtr _hwndMain;

    // GC 방지: native hook이 참조하는 delegate를 static 필드에 보관.
    // 로컬/람다로 생성하면 GC 수집 → access violation crash.
    private static User32.WinEventProc? _imeChangeCallback;

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 3-tier fallback으로 IME 상태를 감지한다 (auto 모드).
    /// </summary>
    public static ImeState Detect(IntPtr hwndFocus, uint threadId)
    {
        // Tier 1: ImmGetDefaultIMEWnd + SendMessageTimeoutW
        ImeState? result = TryTier1(hwndFocus);
        if (result.HasValue) return result.Value;

        // Tier 2: ImmGetContext + ImmGetConversionStatus
        result = TryTier2(hwndFocus);
        if (result.HasValue) return result.Value;

        // Tier 3: GetKeyboardLayout (항상 성공)
        return TryTier3(threadId);
    }

    /// <summary>
    /// DetectionMethod에 따라 분기하여 IME 상태를 감지한다.
    /// </summary>
    public static ImeState Detect(IntPtr hwndFocus, uint threadId, DetectionMethod method)
    {
        return method switch
        {
            DetectionMethod.ImeDefault => TryTier1(hwndFocus) ?? ImeState.English,
            DetectionMethod.ImeContext => TryTier2(hwndFocus) ?? ImeState.English,
            DetectionMethod.KeyboardLayout => TryTier3(threadId),
            _ => Detect(hwndFocus, threadId),  // Auto
        };
    }

    /// <summary>
    /// SetWinEventHook으로 IME 변경 이벤트를 등록한다.
    /// 반드시 메인 스레드(메시지 루프 보유)에서 호출해야 한다.
    /// </summary>
    public static void RegisterHook(IntPtr hwndMain)
    {
        _hwndMain = hwndMain;
        _imeChangeCallback = OnImeChange;

        _hEventHook = User32.SetWinEventHook(
            Win32Constants.EVENT_OBJECT_IME_CHANGE,
            Win32Constants.EVENT_OBJECT_IME_CHANGE,
            IntPtr.Zero,
            _imeChangeCallback,
            0, 0,
            Win32Constants.WINEVENT_OUTOFCONTEXT);

        if (_hEventHook == IntPtr.Zero)
            Logger.Warning("SetWinEventHook for IME change failed");
        else
            Logger.Info("IME change hook registered");
    }

    /// <summary>
    /// SetWinEventHook을 해제한다.
    /// </summary>
    public static void UnregisterHook()
    {
        if (_hEventHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_hEventHook);
            _hEventHook = IntPtr.Zero;
            Logger.Info("IME change hook unregistered");
        }
    }

    // ================================================================
    // 3-tier 내부 구현
    // ================================================================

    /// <summary>
    /// Tier 1: ImmGetDefaultIMEWnd + SendMessageTimeoutW.
    /// SendMessage 사용 금지 — hang 방지를 위해 반드시 Timeout 버전 사용.
    /// </summary>
    private static ImeState? TryTier1(IntPtr hwndFocus)
    {
        IntPtr hIMEWnd = Imm32.ImmGetDefaultIMEWnd(hwndFocus);
        if (hIMEWnd == IntPtr.Zero) return null;

        // 1. IME 오픈 여부 확인
        IntPtr ret = User32.SendMessageTimeoutW(
            hIMEWnd,
            Win32Constants.WM_IME_CONTROL,
            (nint)Win32Constants.IMC_GETOPENSTATUS,
            IntPtr.Zero,
            Win32Constants.SMTO_ABORTIFHUNG,
            DefaultConfig.ImeMessageTimeoutMs,
            out IntPtr openResult);

        if (ret == IntPtr.Zero) return null;  // 타임아웃 또는 에러
        // IME 비활성(openResult=0): 한국어 로케일이면 "영문 입력"이지만, 비-한국어
        // 로케일(일본어/중국어)에서도 openResult=0 이므로 여기서 English 로 단정하면
        // Tier 3 의 NonKorean 판별 기회를 잃는다. null 을 돌려 Tier 2→Tier 3 체인에
        // 위임 — 대부분의 비-한국어 IME 윈도우는 ImmGetContext=0 이라 Tier 2 도 null
        // 로 패스-스루되어 Tier 3 가 GetKeyboardLayout 으로 langId 를 확인한다.
        if (openResult == IntPtr.Zero) return null;

        // 2. IME 오픈 상태 → 변환 모드로 한/영 판별
        //    (GETOPENSTATUS만으로는 한국어 IME 내 한/영 전환을 구분할 수 없음)
        ret = User32.SendMessageTimeoutW(
            hIMEWnd,
            Win32Constants.WM_IME_CONTROL,
            (nint)Win32Constants.IMC_GETCONVERSIONMODE,
            IntPtr.Zero,
            Win32Constants.SMTO_ABORTIFHUNG,
            DefaultConfig.ImeMessageTimeoutMs,
            out IntPtr convResult);

        if (ret == IntPtr.Zero) return null;

        return ((uint)(nint)convResult & Win32Constants.IME_CMODE_HANGUL) != 0
            ? ImeState.Hangul
            : ImeState.English;
    }

    /// <summary>
    /// Tier 2: ImmGetContext + ImmGetConversionStatus.
    /// ImmReleaseContext는 반드시 finally 블록에서 호출.
    /// </summary>
    private static ImeState? TryTier2(IntPtr hwndFocus)
    {
        IntPtr hIMC = Imm32.ImmGetContext(hwndFocus);
        if (hIMC == IntPtr.Zero) return null;

        try
        {
            if (Imm32.ImmGetConversionStatus(hIMC, out uint conversion, out _))
            {
                return (conversion & Win32Constants.IME_CMODE_HANGUL) != 0
                    ? ImeState.Hangul
                    : ImeState.English;
            }
            return null;
        }
        finally
        {
            Imm32.ImmReleaseContext(hwndFocus, hIMC);
        }
    }

    /// <summary>
    /// Tier 3: GetKeyboardLayout. 항상 성공.
    /// 한국어 IME의 한/영 세부 구분은 불가 — NonKorean 판별용.
    /// </summary>
    private static ImeState TryTier3(uint threadId)
    {
        IntPtr hkl = User32.GetKeyboardLayout(threadId);
        ushort langId = (ushort)((long)hkl & Win32Constants.HKL_LANGID_MASK);

        if (langId == Win32Constants.LANGID_KOREAN)
            return ImeState.English;  // 한국어 IME이지만 한/영 구분 불가

        return ImeState.NonKorean;
    }

    // ================================================================
    // WinEvent 콜백
    // ================================================================

    /// <summary>
    /// EVENT_OBJECT_IME_CHANGE 콜백.
    /// WINEVENT_OUTOFCONTEXT → SetWinEventHook를 호출한 스레드(메인)의 메시지 루프에서 발화.
    /// </summary>
    private static void OnImeChange(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        IntPtr hwndFg = User32.GetForegroundWindow();
        if (hwndFg == IntPtr.Zero) return;

        uint threadId = User32.GetWindowThreadProcessId(hwndFg, out _);
        ImeState newState = Detect(hwndFg, threadId);

        if (newState != _lastState)
        {
            _lastState = newState;
            User32.PostMessageW(_hwndMain, AppMessages.WM_IME_STATE_CHANGED,
                (nint)(int)newState, IntPtr.Zero);

            Logger.Debug($"IME state changed: {newState} (via hook)");
        }
    }
}
