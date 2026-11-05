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
        // 후속 렌더 필수 — 아래 HandleDpiChanged 의 주석 참조 (확정 #6).
        RefreshVisibleIndicator();
    }

    /// <summary>
    /// 보이는 배지의 색·크기·위치를 <b>제자리에서</b> 갱신한다 (per-app resolved). config 리로드 / 디스플레이 변경 / 시스템 색
    /// 변경 / DPI / 전원 복귀에서 쓴다. HandleConfigChanged 는 추가로 !UserHidden 가드 후 호출 (숨김 상태 인디를 깨우지 않도록).
    ///
    /// <para>
    /// <b>표시 이벤트가 아니다</b> (bug-hunt 2026-11-05). 예전엔 ShowIndicatorAtForeground → Animation.TriggerShow 를 다시 돌려
    /// 애니메이션 단계를 처음으로 되돌렸다 — 자연 페이드아웃 중(가시 플래그는 페이드가 끝나야 내려간다)인 배지가 되살아나
    /// 표시 시간이 처음부터 다시 흘렀고, Always 모드의 흐려진(idle) 배지가 IME 이벤트 없이 밝아졌다. 배경화면 전환·환경변수
    /// 변경 같은 무관한 시스템 브로드캐스트마다 그랬다. 지금은 단계·타이머를 건드리지 않고 다시 그리기만 한다.
    /// </para>
    ///
    /// <para>
    /// 새 설정에서 배지 주인 앱의 프로필이 꺼졌으면(enabled:false) 숨긴다 — 예전엔 글로벌 스타일로 다시 그려 밝혔다가 감지
    /// 루프의 히스테리시스(3폴링) 뒤에야 숨겼다.
    /// </para>
    /// </summary>
    private static void RefreshVisibleIndicator()
    {
        if (_indicatorVisible && _lastForegroundHwnd != IntPtr.Zero)
        {
            if (!TryResolveCurrent(out AppConfig resolved))
            {
                HideOverlay("app profile disabled by config change");
                return;
            }
            if (IsHiddenByLock) return;
            PaintForPositioning(_lastImeState, resolved);   // 위치 계산 전 크기 확정 — 확정 #24
            var (x, y) = GetAppPosition(resolved);
            _indicatorVisible = Animation.Refresh(x, y, _lastImeState, resolved);
        }
        else if (_indicatorVisible)
        {
            // 포그라운드 hwnd 를 아직 모르는 상태(부팅 직후·필터 구간 통과 전)에서는 위치를 다시
            // 잡을 수 없지만 색·스타일은 갱신할 수 있다. 트레이 메뉴 경로에만 있던 분기를 여기로
            // 올려 세 경로가 같은 헬퍼를 쓰게 한다 (P4 — bug-hunt 3차 C 통합의 전제).
            Overlay.UpdateColor(_lastImeState, ResolveCurrent());
        }
    }

    private static void HandleDisplayChange()
    {
        Logger.Info("Display changed");
        Overlay.HandleDpiChanged();

        RefreshVisibleIndicator();
    }

    private static void HandleSettingChange()
    {
        // 설정 갱신 트랜잭션 안(저장·리로드·전이 적용의 블로킹 대기에서 sent 로 끼어든 경우)이면 보류한다 — 바깥 프레임이
        // 끝나며 `_config = …` 로 이 변경을 덮기 때문이다. 트랜잭션 끝(EndConfigTxn)에서 한 번 처리한다 (bug-hunt 2026-11-05).
        if (_configTxnDepth > 0)
        {
            _settingChangePending = true;
            return;
        }

        BeginConfigTxn();   // 아래 블로킹 셸 호출(트레이 HICON 갱신) 중 같은 브로드캐스트가 다시 오면 끝에 한 번으로 합친다
        try
        {
            HandleSettingChangeCore();
        }
        finally
        {
            EndConfigTxn();
        }
    }

    private static void HandleSettingChangeCore()
    {
        // 시스템 강조색 / 다크 모드 변경 시 프로필 머지 캐시 무효화 — 캐시된 머지 결과가
        // 옛 시스템 색을 박제하고 있을 수 있다 (프로필이 theme=system 을 상속하는 케이스).
        //
        // **무효화는 새 _config 게시 뒤여야 한다** (AUDIT-2026-07-30 §N-59). 앞에 두면
        // 비운 직후~게시 전의 짧은 창에 감지 스레드가 ResolveForApp 으로 **옛 _config 를 써서
        // 캐시를 다시 채우고**, 새 인스턴스가 게시된 뒤에도 옛 색이 그대로 남는다.
        bool themeFollowsSystem = _config.Theme == Theme.System;
        if (themeFollowsSystem)
            _config = ThemePresets.Apply(_config);

        Settings.ClearProfileCache();

        if (themeFollowsSystem)
        {
            Overlay.HandleConfigChanged(_config);

            // **커서 헤일로도 같은 색을 읽는다** — CursorOverlay.BuildStyle 이
            // config.HangulBg / EnglishBg / NonKoreanBg 에서 동심원 색을 뽑는다. 그런데
            // CursorOverlay 는 자체 _config 스냅샷을 들고 있고 갱신 진입점이
            // ApplyCursorConfigChange 하나뿐이라, 이 경로에서 호출하지 않으면 **영구히** 옛 테마
            // 색으로 그린다(인스턴스가 안 바뀌므로 자가치유도 없다). 배지와 트레이만 새 색으로
            // 바뀌고 헤일로만 남는 상태가 다음 config 리로드나 트레이 토글까지 지속됐다
            // (bug-hunt 2026-08-02 확정 #33·#49).
            ApplyCursorConfigChange();
        }

        // PR-13: 글로벌 재적용 후 per-app resolved 로 렌더 — 프로필이 theme=system 상속 시
        //         프로필 색상 6쌍이 새 시스템 색으로 재계산된 인스턴스로 갱신된다. 제자리 갱신이라 무관한 브로드캐스트
        //         (작업 표시줄 이동의 SPI_SETWORKAREA 등)에도 단계·표시 시간은 그대로다 — RefreshVisibleIndicator 참조.
        RefreshVisibleIndicator();

        // 트레이 아이콘도 같은 색 6쌍(Bg/Fg)을 읽어 그리므로 함께 갱신 — 누락 시 셸이 이전
        // HICON 을 계속 들고 있어 다크/라이트 전환 후 트레이만 옛 색으로 남는다 (다음 IME
        // 전환까지). HandleConfigChanged 의 동일 갱신과 대칭. 시스템 색을 따르지 않으면 아이콘 색이 바뀔 일이 없다 —
        // 브로드캐스트마다 HICON 재생성(블로킹 셸 호출)을 하지 않는다.
        if (themeFollowsSystem && _config.TrayEnabled)
            Tray.UpdateState(_lastImeState, _config);
    }

    /// <summary>배지 창이 받은 WM_DPICHANGED (커서 헤일로 창 사본은 WndProc 에서 거른다 — 확정 #1).</summary>
    /// <param name="newDpiX">wParam LOWORD. lParam(RECT* 권장 크기)은 미사용 — 레이어드 창이라 크기는 DIB 가 정한다.</param>
    private static void HandleDpiChanged(uint newDpiX)
    {
        // 숨긴 배지를 다른 배율 모니터에 처음 띄우면 Overlay.Show 가 이미 새 DPI 로 그린 뒤, 그 블리트
        // (UpdateLayeredWindow) **안에서** 이 메시지가 동기로 도착한다. 할 일이 없는데 예전엔 캐시를 지워
        // 빈 DIB 로 바꿨고, 가시 플래그는 TriggerShow 반환 뒤에야 서서 아래 재렌더도 건너뛰어 배지가 표시
        // 시간 내내 투명했다(bug-hunt 2026-11-04 확정 #37·#63). 엔진 DPI 가 이미 같으면 무시한다.
        if (!Overlay.HandleWindowDpiChanged(newDpiX)) return;

        // **후속 렌더가 반드시 필요하다.** Overlay.HandleDpiChanged 는 캐시를 무효화하고
        // PrepareResources 로 새 DIB 섹션을 만들지만 **그리지는 않는다** — 그 시점의 _memDC 는 전
        // 픽셀이 0 인 빈 비트맵이다. 그 상태에서 Render 없이 블리트만 하는 애니메이션 프레임
        // (UpdateAlpha / UpdatePosition / UpdateScaledSize)이 먼저 돌면 빈 DIB 가
        // UpdateLayeredWindow 로 올라가 **배지가 통째로 사라진다**. 형제 경로
        // (HandleDisplayChange · HandleSettingChange · HandleConfigChanged)는 모두 직후
        // RefreshVisibleIndicator 를 부르는데 이 경로와 전원 복귀만 빠져 있었다
        // (bug-hunt 2026-08-02 확정 #6). app.manifest 가 PerMonitorV2 라 WM_DPICHANGED 를 실제로 받는다.
        RefreshVisibleIndicator();
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
