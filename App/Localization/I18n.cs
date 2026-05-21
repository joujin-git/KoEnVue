using System.Collections.Generic;
using KoEnVue.App.Detector;
using KoEnVue.App.Models;
using KoEnVue.Core.Native;

namespace KoEnVue.App.Localization;

/// <summary>
/// 한글/영문 UI 텍스트 관리. P2: UI 텍스트 한글 기본.
/// InvariantGlobalization: true → CultureInfo 사용 불가, P/Invoke로 시스템 언어 감지.
///
/// <para>
/// 내부 저장소는 <see cref="I18nKey"/> → (Ko, En) 튜플 딕셔너리. public surface 는 기존
/// property 이름을 유지해 호출자 영향 0. 3번째 언어를 추가하려면 (1) 튜플을 3-tuple 로
/// 확장하거나 (2) 언어 차원을 또 다른 enum/딕셔너리로 늘리면 된다.
/// </para>
/// </summary>
internal static class I18n
{
    private static bool _isKorean = true;  // P2: 한글 기본

    /// <summary>현재 한국어 모드 여부.</summary>
    public static bool IsKorean => _isKorean;

    // ================================================================
    // 초기화
    // ================================================================

    /// <summary>
    /// 언어 설정 로드. <see cref="AppLanguage.Auto"/> 시 Windows 시스템 UI 언어가
    /// 한국어이면 ko, 아니면 en. P2: 알 수 없는 값은 한글 fallback.
    /// </summary>
    public static void Load(AppLanguage language)
    {
        _isKorean = language switch
        {
            AppLanguage.En => false,
            AppLanguage.Ko => true,
            AppLanguage.Auto => IsSystemKorean(),
            _ => true,  // P2: 한글 기본
        };
    }

    /// <summary>
    /// Windows 시스템 UI 언어가 한국어인지 확인.
    /// GetUserDefaultUILanguage() LANGID == 0x0412.
    /// </summary>
    public static bool IsSystemKorean()
    {
        ushort langId = Kernel32.GetUserDefaultUILanguage();
        return langId == ImeConstants.LANGID_KOREAN;
    }

    // ================================================================
    // 키 enum — 신규 항목은 끝에 append (값 안정성 불요, 키 일치만 보장)
    // ================================================================

    private enum I18nKey
    {
        // 트레이 메뉴 — 투명도
        OpacityHigh, OpacityNormal, OpacityLow,

        // 트레이 메뉴 — 메인
        MenuOpacity, MenuSize, MenuStartup,
        MenuDefaultPosition, MenuDefaultPosSetCurrent, MenuDefaultPosReset,
        MenuSnapToWindows, MenuAnimation, MenuChangeHighlight,
        MenuPositionMode, MenuPositionFixed, MenuPositionWindow,
        MenuDragModifier, MenuDragModifierNone, MenuDragModifierCtrl,
        MenuDragModifierAlt, MenuDragModifierCtrlAlt,
        MenuCleanup, MenuUserHidden, MenuSettings, MenuExit,
        MenuDownload,
        MenuSizeCustom,

        // 상세 설정 다이얼로그
        SettingsDialogTitle, SettingsDialogDescription,
        SettingsInvalidNumberFmt, SettingsOutOfRangeFmt,
        SettingsInvalidColorFmt, SettingsEmptyNotAllowedFmt,

        // 크기 직접 지정 다이얼로그
        ScaleDialogTitle, ScaleDialogPrompt, ScaleDialogHint,
        ScaleDialogOk, ScaleDialogCancel,
        ScaleDialogInvalidInput, ScaleDialogOutOfRange,

        // 트레이 메시지 박스
        TrayPositionUnavailable, TrayPositionHistoryEmpty,
        RunningSuffix,

        // 트레이 툴팁
        TooltipHangul, TooltipEnglish, TooltipNonKorean,

        // 크기 배율 라벨 — 숫자 뒤에 붙는 단위 접미사 ("2배" vs "2x")
        SizeLabelSuffix,
    }

    private static readonly Dictionary<I18nKey, (string Ko, string En)> _table = new()
    {
        // 트레이 메뉴 — 투명도
        [I18nKey.OpacityHigh]              = ("진하게", "High"),
        [I18nKey.OpacityNormal]            = ("보통", "Normal"),
        [I18nKey.OpacityLow]               = ("연하게", "Low"),

        // 트레이 메뉴 — 메인
        [I18nKey.MenuOpacity]              = ("투명도", "Opacity"),
        [I18nKey.MenuSize]                 = ("크기", "Size"),
        [I18nKey.MenuStartup]              = ("시작 프로그램 등록", "Start with Windows"),
        [I18nKey.MenuDefaultPosition]      = ("기본 위치", "Default Position"),
        [I18nKey.MenuDefaultPosSetCurrent] = ("현재 위치로 설정", "Set to Current Position"),
        [I18nKey.MenuDefaultPosReset]      = ("초기화", "Reset"),
        [I18nKey.MenuSnapToWindows]        = ("창에 자석처럼 붙이기", "Snap to Windows"),
        [I18nKey.MenuAnimation]            = ("애니메이션 사용", "Animations enabled"),
        [I18nKey.MenuChangeHighlight]      = ("변경 시 강조", "Highlight on change"),
        [I18nKey.MenuPositionMode]         = ("위치 모드", "Position Mode"),
        [I18nKey.MenuPositionFixed]        = ("고정 위치", "Fixed Position"),
        [I18nKey.MenuPositionWindow]       = ("창 기준", "Relative to Window"),
        [I18nKey.MenuDragModifier]         = ("드래그 활성 키", "Drag Modifier"),
        [I18nKey.MenuDragModifierNone]     = ("없음", "None"),
        [I18nKey.MenuDragModifierCtrl]     = ("Ctrl", "Ctrl"),
        [I18nKey.MenuDragModifierAlt]      = ("Alt", "Alt"),
        [I18nKey.MenuDragModifierCtrlAlt]  = ("Ctrl + Alt", "Ctrl + Alt"),
        [I18nKey.MenuCleanup]              = ("위치 기록 정리...", "Clean position history..."),
        [I18nKey.MenuUserHidden]           = ("인디케이터 숨김", "Hide indicator"),
        [I18nKey.MenuSettings]             = ("상세 설정...", "Settings..."),
        [I18nKey.MenuExit]                 = ("종료", "Exit"),
        [I18nKey.MenuDownload]             = ("다운로드", "Download"),
        [I18nKey.MenuSizeCustom]           = ("직접 지정...", "Custom..."),

        // 상세 설정 다이얼로그
        [I18nKey.SettingsDialogTitle]       = ("상세 설정", "Settings"),
        [I18nKey.SettingsDialogDescription] = (
            "설정을 변경한 후 '확인'을 눌러 적용하세요.",
            "Change settings and click 'OK' to apply."),
        [I18nKey.SettingsInvalidNumberFmt]  = ("{0}: 올바른 숫자가 아닙니다.", "{0}: Invalid number."),
        [I18nKey.SettingsOutOfRangeFmt]     = ("{0}: {1}에서 {2} 사이 값이어야 합니다.", "{0}: Must be between {1} and {2}."),
        [I18nKey.SettingsInvalidColorFmt]   = ("{0}: #RRGGBB 형식이어야 합니다.", "{0}: Must be in #RRGGBB format."),
        [I18nKey.SettingsEmptyNotAllowedFmt]= ("{0}: 비워둘 수 없습니다.", "{0}: Cannot be empty."),

        // 크기 직접 지정 다이얼로그
        [I18nKey.ScaleDialogTitle]          = ("크기 배율 직접 지정", "Custom Indicator Scale"),
        [I18nKey.ScaleDialogPrompt]         = ("배율 (1.0 ~ 5.0):", "Scale (1.0 ~ 5.0):"),
        [I18nKey.ScaleDialogHint]           = ("소수점 첫째 자리까지 입력할 수 있습니다.", "Up to one decimal place."),
        [I18nKey.ScaleDialogOk]             = ("확인", "OK"),
        [I18nKey.ScaleDialogCancel]         = ("취소", "Cancel"),
        [I18nKey.ScaleDialogInvalidInput]   = (
            "올바른 숫자가 아닙니다. 1.0에서 5.0 사이 값을 입력하세요.",
            "Invalid number. Enter a value between 1.0 and 5.0."),
        [I18nKey.ScaleDialogOutOfRange]     = (
            "1.0에서 5.0 사이 값만 입력할 수 있습니다.",
            "Value must be between 1.0 and 5.0."),

        // 트레이 메시지 박스
        [I18nKey.TrayPositionUnavailable]   = (
            "인디케이터 위치를 확인할 수 없습니다. 잠시 후 다시 시도하세요.",
            "Cannot determine current indicator position. Please try again shortly."),
        [I18nKey.TrayPositionHistoryEmpty]  = ("저장된 위치 기록이 없습니다.", "No saved position history."),
        [I18nKey.RunningSuffix]             = (" (실행 중)", " (running)"),

        // 트레이 툴팁
        [I18nKey.TooltipHangul]             = ("한글 모드", "Hangul Mode"),
        [I18nKey.TooltipEnglish]            = ("영문 모드", "English Mode"),
        [I18nKey.TooltipNonKorean]          = ("영문 모드 (비한국어)", "English (Non-Korean)"),

        [I18nKey.SizeLabelSuffix]           = ("배", "x"),
    };

    /// <summary>
    /// 키 → 현재 언어 문자열. 미정의 키는 KeyNotFoundException — 로직 버그라 전파.
    /// </summary>
    private static string Get(I18nKey key)
    {
        var (ko, en) = _table[key];
        return _isKorean ? ko : en;
    }

    // ================================================================
    // Public surface — 호출자 영향 0 (기존 property 이름·시그너처 유지)
    // ================================================================

    // 트레이 메뉴 — 투명도
    public static string OpacityHigh   => Get(I18nKey.OpacityHigh);
    public static string OpacityNormal => Get(I18nKey.OpacityNormal);
    public static string OpacityLow    => Get(I18nKey.OpacityLow);

    // 트레이 메뉴 — 메인
    public static string MenuOpacity              => Get(I18nKey.MenuOpacity);
    public static string MenuSize                 => Get(I18nKey.MenuSize);
    public static string MenuStartup              => Get(I18nKey.MenuStartup);
    public static string MenuDefaultPosition      => Get(I18nKey.MenuDefaultPosition);
    public static string MenuDefaultPosSetCurrent => Get(I18nKey.MenuDefaultPosSetCurrent);
    public static string MenuDefaultPosReset      => Get(I18nKey.MenuDefaultPosReset);
    public static string MenuSnapToWindows        => Get(I18nKey.MenuSnapToWindows);
    public static string MenuAnimation            => Get(I18nKey.MenuAnimation);
    public static string MenuChangeHighlight      => Get(I18nKey.MenuChangeHighlight);
    public static string MenuPositionMode         => Get(I18nKey.MenuPositionMode);
    public static string MenuPositionFixed        => Get(I18nKey.MenuPositionFixed);
    public static string MenuPositionWindow       => Get(I18nKey.MenuPositionWindow);
    public static string MenuDragModifier         => Get(I18nKey.MenuDragModifier);
    public static string MenuDragModifierNone     => Get(I18nKey.MenuDragModifierNone);
    public static string MenuDragModifierCtrl     => Get(I18nKey.MenuDragModifierCtrl);
    public static string MenuDragModifierAlt      => Get(I18nKey.MenuDragModifierAlt);
    public static string MenuDragModifierCtrlAlt  => Get(I18nKey.MenuDragModifierCtrlAlt);
    public static string MenuCleanup              => Get(I18nKey.MenuCleanup);
    public static string MenuUserHidden           => Get(I18nKey.MenuUserHidden);
    public static string MenuSettings             => Get(I18nKey.MenuSettings);
    public static string MenuExit                 => Get(I18nKey.MenuExit);

    /// <summary>
    /// 트레이 메뉴 최상단 헤더의 행위 단어 — 새 버전이 가용한 상태에서만 라벨 끝에 붙는다.
    /// 평소 헤더는 `KoEnVue v{ver} — GitHub` (브랜드/도메인 그대로), 업데이트 가용 시
    /// `KoEnVue v{cur} → {tag} — 다운로드` 형태로 이 단어만 I18n 분기된다.
    /// </summary>
    public static string MenuDownload => Get(I18nKey.MenuDownload);

    // 상세 설정 다이얼로그
    public static string SettingsDialogTitle        => Get(I18nKey.SettingsDialogTitle);
    public static string SettingsDialogDescription  => Get(I18nKey.SettingsDialogDescription);

    /// <summary>필드 이름과 원본 텍스트를 받아 오류 메시지 형식 문자열을 반환.</summary>
    public static string SettingsInvalidNumberFmt   => Get(I18nKey.SettingsInvalidNumberFmt);
    public static string SettingsOutOfRangeFmt      => Get(I18nKey.SettingsOutOfRangeFmt);
    public static string SettingsInvalidColorFmt    => Get(I18nKey.SettingsInvalidColorFmt);
    public static string SettingsEmptyNotAllowedFmt => Get(I18nKey.SettingsEmptyNotAllowedFmt);

    /// <summary>크기 배율 서브메뉴 항목 라벨 (1x~5x).</summary>
    public static string GetSizeLabel(int scale) => $"{scale}{Get(I18nKey.SizeLabelSuffix)}";

    /// <summary>"직접 지정..." 메뉴 항목 기본 라벨.</summary>
    public static string MenuSizeCustom => Get(I18nKey.MenuSizeCustom);

    /// <summary>
    /// 현재 배율이 비정수일 때 "직접 지정 (2.3배)" 형태로 현재 값을 노출.
    /// 정수 값(2.0, 3.0 등)은 해당 정수 프리셋으로 체크되므로 이 메서드를 거치지 않는다.
    /// </summary>
    public static string FormatCustomScaleLabel(double scale) =>
        $"{MenuSizeCustom} ({scale:0.#}{Get(I18nKey.SizeLabelSuffix)})";

    // 크기 직접 지정 다이얼로그
    public static string ScaleDialogTitle        => Get(I18nKey.ScaleDialogTitle);
    public static string ScaleDialogPrompt       => Get(I18nKey.ScaleDialogPrompt);
    public static string ScaleDialogHint         => Get(I18nKey.ScaleDialogHint);
    public static string ScaleDialogOk           => Get(I18nKey.ScaleDialogOk);
    public static string ScaleDialogCancel       => Get(I18nKey.ScaleDialogCancel);
    public static string ScaleDialogInvalidInput => Get(I18nKey.ScaleDialogInvalidInput);
    public static string ScaleDialogOutOfRange   => Get(I18nKey.ScaleDialogOutOfRange);

    // 트레이 메시지 박스
    /// <summary>
    /// 현재 위치 저장 시 오버레이 좌표를 얻지 못한 경우 노출되는 에러 메시지.
    /// </summary>
    public static string TrayPositionUnavailable  => Get(I18nKey.TrayPositionUnavailable);

    /// <summary>위치 기록 정리 시 저장된 항목이 없을 때 노출되는 안내 메시지.</summary>
    public static string TrayPositionHistoryEmpty => Get(I18nKey.TrayPositionHistoryEmpty);

    /// <summary>
    /// 위치 기록 정리 다이얼로그에서 "실행 중" 프로세스 옆에 표시되는 접미사.
    /// 선행 공백 포함 (원본 프로세스명 뒤에 바로 이어 붙으므로).
    /// </summary>
    public static string RunningSuffix => Get(I18nKey.RunningSuffix);

    // 트레이 툴팁
    public static string TooltipHangul    => Get(I18nKey.TooltipHangul);
    public static string TooltipEnglish   => Get(I18nKey.TooltipEnglish);
    public static string TooltipNonKorean => Get(I18nKey.TooltipNonKorean);

    /// <summary>
    /// IME 상태에 따른 트레이 툴팁 텍스트 반환.
    /// </summary>
    public static string GetTrayTooltip(ImeState state) => state switch
    {
        ImeState.Hangul => TooltipHangul,
        ImeState.English => TooltipEnglish,
        ImeState.NonKorean => TooltipNonKorean,
        _ => "KoEnVue",
    };
}
