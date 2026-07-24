using KoEnVue.App.Config;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue;

/// <summary>
/// 전원/디스플레이/시스템 색/DPI/세션 잠금 · TaskbarCreated 브로드캐스트.
/// </summary>
internal static partial class Program
{
    /// <summary>
    /// Explorer 재시작(업데이트, 크래시 복구) 시 셸이 브로드캐스트하는 TaskbarCreated 메시지 핸들러.
    /// 셸은 재시작 시 모든 트레이 아이콘 등록 정보를 잃으므로 앱이 스스로 재등록해야 한다.
    /// </summary>
    private static void HandleTaskbarCreated()
    {
        Logger.Info("TaskbarCreated broadcast received, recreating tray icon");
        if (_config.TrayEnabled)
            Tray.Recreate(_lastImeState, _config);
    }

    private static void HandlePowerResume()
    {
        Logger.Info("Power resumed");
        Overlay.HandleDpiChanged();
    }

    /// <summary>인디가 가시 상태(+ 포그라운드 유효)면 현재 위치로 TriggerShow 재호출 (per-app resolved,
    /// imeChanged=false). config 리로드 / 디스플레이 변경 / 시스템 색 변경 등에서 가시 인디를 즉시 갱신한다.
    /// HandleConfigChanged 는 추가로 !UserHidden 가드 후 호출 (숨김 상태 인디를 깨우지 않도록).</summary>
    private static void RefreshVisibleIndicator()
    {
        if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
            ShowIndicatorAtForeground(_lastImeState, ResolveCurrent(), imeChanged: false);
    }

    private static void HandleDisplayChange()
    {
        Logger.Info("Display changed");
        Overlay.HandleDpiChanged();

        RefreshVisibleIndicator();
    }

    private static void HandleSettingChange()
    {
        // 시스템 강조색 / 다크 모드 변경 시 프로필 머지 캐시 무효화 — 캐시된 머지 결과가
        // 옛 시스템 색을 박제하고 있을 수 있다 (프로필이 theme=system 을 상속하는 케이스).
        Settings.ClearProfileCache();

        if (_config.Theme == Theme.System)
        {
            _config = ThemePresets.Apply(_config);
            Overlay.HandleConfigChanged(_config);
        }

        // PR-13: 글로벌 재적용 후 per-app resolved 로 렌더 — 프로필이 theme=system 상속 시
        //         프로필 색상 6쌍이 새 시스템 색으로 재계산된 인스턴스로 갱신된다.
        RefreshVisibleIndicator();
    }

    private static void HandleDpiChanged()
    {
        // WM_DPICHANGED 페이로드(wParam: HIWORD/LOWORD=newDpiY/newDpiX, lParam: RECT* 권장 크기)는
        // 현재 미사용 — Overlay 가 자체 DPI 재조회로 처리한다. per-monitor DPI 정밀 대응이 필요해지면
        // dispatch 의 WM_DPICHANGED case 에서 wParam/lParam 을 다시 전달하면 된다(WndProc 에서 항상 가용).
        Overlay.HandleDpiChanged();
    }

    /// <summary>
    /// WM_WTSSESSION_CHANGE — 잠금 시 배지 즉시 숨김. 해제 시 감지 스레드가 다음 포그라운드
    /// 이벤트로 자연 복원하므로 별도 show 호출 없음. LOCK/UNLOCK 외 이벤트(로그오프 등)는 무시.
    /// </summary>
    private static void HandleSessionChange(uint sessionEvent)
    {
        if (sessionEvent == Win32Constants.WTS_SESSION_LOCK)
        {
            _sessionLocked = true;
            Logger.Info("Session locked");
            if (_config.HideOnLockScreen && _indicatorVisible)
                HideOverlay("Session lock");
        }
        else if (sessionEvent == Win32Constants.WTS_SESSION_UNLOCK)
        {
            _sessionLocked = false;
            Logger.Info("Session unlocked");
        }
    }
}
