using KoEnVue.App.Bootstrap;
using KoEnVue.App.Config;
using KoEnVue.App.Localization;
using KoEnVue.App.Models;
using KoEnVue.App.Startup;
using KoEnVue.App.UI.Dialogs;
using KoEnVue.Core.Native;

namespace KoEnVue.App.UI;

/// <summary>
/// 트레이 팝업 메뉴 빌더. <see cref="Tray"/> 의 partial 분할 (PR-04 god class 분해 후속) —
/// 메뉴 항목 생성 + 라디오/체크 상태 결정만 담당하며, 라이프사이클 / 디스패치 / helpers 는 본 파일이 아니다.
/// </summary>
internal static partial class Tray
{
    /// <summary>
    /// 트레이 우클릭 팝업 메뉴 표시.
    /// </summary>
    internal static void ShowMenu(IntPtr hwndMain, AppConfig config)
    {
        if (!_initialized) return;

        // --- 서브메뉴: 투명도 ---
        IntPtr hOpacityMenu = User32.CreatePopupMenu();
        double[] presets = config.TrayQuickOpacityPresets;
        if (presets.Length >= 3)
        {
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_HIGH,
                $"{I18n.OpacityHigh} {presets[0]}");
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_NORMAL,
                $"{I18n.OpacityNormal} {presets[1]}");
            User32.AppendMenuW(hOpacityMenu, Win32Constants.MF_STRING, (nuint)IDM_OPACITY_LOW,
                $"{I18n.OpacityLow} {presets[2]}");

            // Always 모드는 ActiveOpacity 가 실제 적용 값이라 이를 기준으로 라디오 매칭.
            double effectiveOpacity = config.DisplayMode == DisplayMode.Always
                ? config.ActiveOpacity
                : config.Opacity;
            uint opacityCheckId = 0;
            if (Math.Abs(effectiveOpacity - presets[0]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_HIGH;
            else if (Math.Abs(effectiveOpacity - presets[1]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_NORMAL;
            else if (Math.Abs(effectiveOpacity - presets[2]) < OpacityTolerance) opacityCheckId = (uint)IDM_OPACITY_LOW;

            if (opacityCheckId != 0)
                User32.CheckMenuRadioItem(hOpacityMenu, (uint)IDM_OPACITY_HIGH, (uint)IDM_OPACITY_LOW,
                    opacityCheckId, Win32Constants.MF_BYCOMMAND);
        }

        // --- 서브메뉴: 크기 배율 (정수 프리셋 5개 + "직접 지정" 다이얼로그) ---
        IntPtr hSizeMenu = User32.CreatePopupMenu();
        for (int n = ScaleIntegerMin; n <= ScaleIntegerMax; n++)
        {
            User32.AppendMenuW(hSizeMenu, Win32Constants.MF_STRING,
                (nuint)(IDM_SIZE_BASE + n - ScaleIntegerMin), I18n.GetSizeLabel(n));
        }

        double currentScale = Math.Clamp(config.IndicatorScale,
            ScaleInputDialog.ScaleMinValue, ScaleInputDialog.ScaleMaxValue);
        bool isIntegerScale = ScaleInputDialog.IsIntegerScale(currentScale);
        // 비정수 배율이면 "직접 지정 (2.3배)" 형태로 값을 라벨에 노출.
        string customLabel = isIntegerScale
            ? I18n.MenuSizeCustom
            : I18n.FormatCustomScaleLabel(currentScale);
        User32.AppendMenuW(hSizeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_SIZE_CUSTOM, customLabel);

        uint sizeCheckId;
        if (isIntegerScale)
        {
            int intScale = Math.Clamp((int)Math.Round(currentScale), ScaleIntegerMin, ScaleIntegerMax);
            sizeCheckId = (uint)(IDM_SIZE_BASE + intScale - ScaleIntegerMin);
        }
        else
        {
            sizeCheckId = (uint)IDM_SIZE_CUSTOM;
        }
        User32.CheckMenuRadioItem(hSizeMenu,
            (uint)IDM_SIZE_BASE,
            (uint)IDM_SIZE_CUSTOM,
            sizeCheckId,
            Win32Constants.MF_BYCOMMAND);

        // --- 서브메뉴: 기본 위치 ---
        IntPtr hDefaultPosMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hDefaultPosMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DEFAULT_POS_SET_CURRENT, I18n.MenuDefaultPosSetCurrent);
        bool hasDefault = config.PositionMode == PositionMode.Window
            ? config.DefaultIndicatorPositionRelative is not null
            : config.DefaultIndicatorPosition is not null;
        uint resetFlags = Win32Constants.MF_STRING
            | (hasDefault ? 0u : Win32Constants.MF_GRAYED);
        User32.AppendMenuW(hDefaultPosMenu, resetFlags,
            (nuint)IDM_DEFAULT_POS_RESET, I18n.MenuDefaultPosReset);

        // --- 서브메뉴: 위치 모드 ---
        IntPtr hPositionModeMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hPositionModeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_POSITION_FIXED, I18n.MenuPositionFixed);
        User32.AppendMenuW(hPositionModeMenu, Win32Constants.MF_STRING,
            (nuint)IDM_POSITION_WINDOW, I18n.MenuPositionWindow);
        uint positionModeCheckId = config.PositionMode == PositionMode.Fixed
            ? (uint)IDM_POSITION_FIXED : (uint)IDM_POSITION_WINDOW;
        User32.CheckMenuRadioItem(hPositionModeMenu, (uint)IDM_POSITION_FIXED,
            (uint)IDM_POSITION_WINDOW, positionModeCheckId, Win32Constants.MF_BYCOMMAND);

        // --- 서브메뉴: 드래그 활성 키 ---
        IntPtr hDragModMenu = User32.CreatePopupMenu();
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_NONE, I18n.MenuDragModifierNone);
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_CTRL, I18n.MenuDragModifierCtrl);
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_ALT, I18n.MenuDragModifierAlt);
        User32.AppendMenuW(hDragModMenu, Win32Constants.MF_STRING,
            (nuint)IDM_DRAG_MOD_CTRL_ALT, I18n.MenuDragModifierCtrlAlt);
        uint dragModCheckId = config.DragModifier switch
        {
            DragModifier.Ctrl    => (uint)IDM_DRAG_MOD_CTRL,
            DragModifier.Alt     => (uint)IDM_DRAG_MOD_ALT,
            DragModifier.CtrlAlt => (uint)IDM_DRAG_MOD_CTRL_ALT,
            _                    => (uint)IDM_DRAG_MOD_NONE,
        };
        User32.CheckMenuRadioItem(hDragModMenu, (uint)IDM_DRAG_MOD_NONE,
            (uint)IDM_DRAG_MOD_CTRL_ALT, dragModCheckId, Win32Constants.MF_BYCOMMAND);

        // --- 메인 메뉴 ---
        IntPtr hMenu = User32.CreatePopupMenu();

        // 헤더 라인 — 메뉴 최상단 단일 진입점. _pendingUpdate 따라 "v{ver} — GitHub" 또는
        // "v{cur} → {newTag} — 다운로드". 볼드 렌더 위해 MF_DEFAULT 플래그 + SetMenuDefaultItem
        // 둘 다 적용 (Win11 일부 환경에서 플래그만으로 시각 적용 안 되는 케이스 보강).
        // _pendingUpdate.Version 은 release tag (v1.0.1) 라 prefix 가 이미 포함됨.
        string headerLabel = _pendingUpdate is not null
            ? $"KoEnVue v{DefaultConfig.AppVersion} → {_pendingUpdate.Version} — {I18n.MenuDownload}"
            : $"KoEnVue v{DefaultConfig.AppVersion} — GitHub";
        User32.AppendMenuW(hMenu,
            Win32Constants.MF_STRING | Win32Constants.MF_DEFAULT,
            (nuint)IDM_HOMEPAGE, headerLabel);
        User32.SetMenuDefaultItem(hMenu, (uint)IDM_HOMEPAGE, 0); // 0 = by command ID (not position)
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);

        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hOpacityMenu, I18n.MenuOpacity);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP, (nuint)(nint)hSizeMenu, I18n.MenuSize);
        uint snapFlags = config.SnapToWindows ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, snapFlags, (nuint)IDM_SNAP_TO_WINDOWS, I18n.MenuSnapToWindows);
        uint animationFlags = config.AnimationEnabled ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, animationFlags, (nuint)IDM_ANIMATION_ENABLED, I18n.MenuAnimation);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        uint highlightFlags = config.ChangeHighlight ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, highlightFlags, (nuint)IDM_CHANGE_HIGHLIGHT, I18n.MenuChangeHighlight);
        // 커서 IME 전환 스케일 팝 on/off — 메인 변경 강조 바로 아래 (강조 항목끼리 인접 배치).
        uint cursorHighlightFlags = config.CursorChangeHighlight ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, cursorHighlightFlags, (nuint)IDM_CURSOR_HIGHLIGHT, I18n.MenuCursorHighlight);
        uint cursorMotionDimFlags = config.CursorMotionDimEnabled ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, cursorMotionDimFlags, (nuint)IDM_CURSOR_MOTION_DIM, I18n.MenuCursorMotionDim);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);

        bool isStartup = StartupTaskManager.IsStartupRegistered();
        User32.AppendMenuW(hMenu, isStartup ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED,
            (nuint)IDM_STARTUP, I18n.MenuStartup);
        // PR-15: 관리자 권한 토글 — UIPI 우회 (admin 콘솔의 한/영 표시). 시작 프로그램 등록 바로 옆에 배치.
        // 체크 표시 = config.AdminElevation OR IsCurrentProcessElevated() (PR-15 후속 fix #4, 2026-05-29).
        // OR 의 이유 — admin 환경 외부 spawn (예: admin Total Commander 가 KoEnVue.exe 실행 시 admin
        // 토큰 상속) 경우, config 가 false 여도 실 권한이 admin 이면 사용자에게 명시적으로 시각 노출.
        // 다른 메뉴 항목 (Snap/Animation 등) 은 config 직접 반영 — admin 항목만 외부 환경 영향 받는
        // 유일한 케이스라 OR 정당. 토글 클릭은 여전히 config 만 변경 (Windows token 모델 한계 — 실
        // 권한은 다음 부팅까지 영향 없음, MessageBoxW 안내가 사용자 가이드).
        //
        // 라벨 hint (PR-15 후속 fix #5, 2026-05-29) — case 2 (config=false + IsElevated=true,
        // admin 환경 외부 spawn) 전용 "(Config = User)" suffix 노출. fix #4 OR 의 visible 정합 +
        // case 2 의 config 값 명시 = 사용자가 두 신호 (실 권한 + config 값) 모두 인지. case 1/3/4
        // 는 기본 라벨 — case 1 일관 OFF / case 3 사용자 명시 토글 결과 / case 4 일관 ON.
        bool isCurrentlyElevated = AdminElevation.IsCurrentProcessElevated();
        bool isAdminEffective = config.AdminElevation || isCurrentlyElevated;
        bool isExternalElevation = !config.AdminElevation && isCurrentlyElevated;  // case 2
        string adminElevationLabel = isExternalElevation
            ? I18n.MenuAdminElevationExternal
            : I18n.MenuAdminElevation;
        uint adminElevationFlags = isAdminEffective ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, adminElevationFlags, (nuint)IDM_ADMIN_ELEVATION, adminElevationLabel);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hDefaultPosMenu, I18n.MenuDefaultPosition);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hPositionModeMenu, I18n.MenuPositionMode);
        User32.AppendMenuW(hMenu, Win32Constants.MF_POPUP,
            (nuint)(nint)hDragModMenu, I18n.MenuDragModifier);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_CLEANUP, I18n.MenuCleanup);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        uint userHiddenFlags = config.UserHidden ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, userHiddenFlags, (nuint)IDM_USER_HIDDEN, I18n.MenuUserHidden);
        // "커서 인디케이터 숨김" — 메인 "메인 인디케이터 숨김" 과 같은 패턴 (MF_CHECKED = 현재 숨김 상태).
        // 라벨이 "숨김" 이므로 체크 = "현재 숨김 중" = CursorIndicatorEnabled false. 클릭 시 enabled 반전.
        uint cursorToggleFlags = !config.CursorIndicatorEnabled ? Win32Constants.MF_CHECKED : Win32Constants.MF_UNCHECKED;
        User32.AppendMenuW(hMenu, cursorToggleFlags, (nuint)IDM_CURSOR_TOGGLE, I18n.MenuCursorIndicator);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_SETTINGS, I18n.MenuSettings);
        User32.AppendMenuW(hMenu, Win32Constants.MF_SEPARATOR, 0, null);
        User32.AppendMenuW(hMenu, Win32Constants.MF_STRING, (nuint)IDM_EXIT, I18n.MenuExit);

        // --- 표시 (워크어라운드 적용) ---
        User32.GetCursorPos(out POINT pt);
        User32.SetForegroundWindow(hwndMain);
        User32.TrackPopupMenu(hMenu, Win32Constants.TPM_RIGHTBUTTON,
            pt.X, pt.Y, 0, hwndMain, IntPtr.Zero);
        User32.PostMessageW(hwndMain, Win32Constants.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        // DestroyMenu 는 MF_POPUP 로 부착된 서브메뉴를 자동 파괴. AppendMenuW(MF_POPUP) 가 모두 성공한
        // 전제이고 P/Invoke 는 BOOL 만 반환해 중단 경로가 없어 누수가 없다.
        User32.DestroyMenu(hMenu);
    }
}
