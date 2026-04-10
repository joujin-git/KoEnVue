using KoEnVue.Models;
using KoEnVue.Native;

namespace KoEnVue.Utils;

/// <summary>
/// 한글/영문 UI 텍스트 관리. P2: UI 텍스트 한글 기본.
/// InvariantGlobalization: true → CultureInfo 사용 불가, P/Invoke로 시스템 언어 감지.
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
    /// 언어 설정 로드. "ko" | "en" | "auto".
    /// auto: Windows 시스템 UI 언어가 한국어이면 ko, 아니면 en.
    /// </summary>
    public static void Load(string language)
    {
        _isKorean = language switch
        {
            "en" => false,
            "ko" => true,
            "auto" => IsSystemKorean(),
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
        return langId == Win32Constants.LANGID_KOREAN;
    }

    // ================================================================
    // 트레이 메뉴 — 투명도
    // ================================================================

    public static string OpacityHigh => _isKorean ? "진하게" : "High";
    public static string OpacityNormal => _isKorean ? "보통" : "Normal";
    public static string OpacityLow => _isKorean ? "연하게" : "Low";

    // ================================================================
    // 트레이 메뉴 — 메인 메뉴
    // ================================================================

    public static string MenuOpacity => _isKorean ? "투명도" : "Opacity";
    public static string MenuSize => _isKorean ? "크기" : "Size";
    public static string MenuStartup => _isKorean ? "시작 프로그램 등록" : "Start with Windows";
    public static string MenuDefaultPosition => _isKorean ? "기본 위치" : "Default Position";
    public static string MenuDefaultPosSetCurrent => _isKorean ? "현재 위치로 설정" : "Set to Current Position";
    public static string MenuDefaultPosReset => _isKorean ? "초기화" : "Reset";
    public static string MenuSnapToWindows => _isKorean ? "창에 자석처럼 붙이기" : "Snap to Windows";
    public static string MenuCleanup => _isKorean ? "미사용 위치 데이터 정리" : "Clean unused position data";
    public static string MenuSettings => _isKorean ? "상세 설정" : "Settings";
    public static string MenuExit => _isKorean ? "종료" : "Exit";

    // ================================================================
    // 상세 설정 대화상자
    // ================================================================

    public static string SettingsDialogTitle => _isKorean ? "상세 설정" : "Settings";
    public static string SettingsDialogDescription =>
        _isKorean
            ? "설정을 변경한 후 '확인'을 눌러 적용하세요."
            : "Change settings and click 'OK' to apply.";

    /// <summary>필드 이름과 원본 텍스트를 받아 오류 메시지 형식 문자열을 반환.</summary>
    public static string SettingsInvalidNumberFmt =>
        _isKorean ? "{0}: 올바른 숫자가 아닙니다." : "{0}: Invalid number.";
    public static string SettingsOutOfRangeFmt =>
        _isKorean ? "{0}: {1}에서 {2} 사이 값이어야 합니다." : "{0}: Must be between {1} and {2}.";
    public static string SettingsInvalidColorFmt =>
        _isKorean ? "{0}: #RRGGBB 형식이어야 합니다." : "{0}: Must be in #RRGGBB format.";
    public static string SettingsEmptyNotAllowedFmt =>
        _isKorean ? "{0}: 비워둘 수 없습니다." : "{0}: Cannot be empty.";

    /// <summary>크기 배율 서브메뉴 항목 라벨 (1x~5x).</summary>
    public static string GetSizeLabel(int scale) => _isKorean ? $"{scale}배" : $"{scale}x";

    /// <summary>"직접 지정" 메뉴 항목 기본 라벨.</summary>
    public static string MenuSizeCustom => _isKorean ? "직접 지정" : "Custom";

    /// <summary>
    /// 현재 배율이 비정수일 때 "직접 지정 (2.3배)" 형태로 현재 값을 노출.
    /// 정수 값(2.0, 3.0 등)은 해당 정수 프리셋으로 체크되므로 이 메서드를 거치지 않는다.
    /// </summary>
    public static string FormatCustomScaleLabel(double scale) =>
        _isKorean ? $"{MenuSizeCustom} ({scale:0.#}배)" : $"{MenuSizeCustom} ({scale:0.#}x)";

    // ================================================================
    // 직접 지정 대화상자
    // ================================================================

    public static string ScaleDialogTitle => _isKorean ? "크기 배율 직접 지정" : "Custom Indicator Scale";
    public static string ScaleDialogPrompt => _isKorean ? "배율 (1.0 ~ 5.0):" : "Scale (1.0 ~ 5.0):";
    public static string ScaleDialogHint =>
        _isKorean ? "소수점 첫째 자리까지 입력할 수 있습니다." : "Up to one decimal place.";
    public static string ScaleDialogOk => _isKorean ? "확인" : "OK";
    public static string ScaleDialogCancel => _isKorean ? "취소" : "Cancel";
    public static string ScaleDialogInvalidInput =>
        _isKorean
            ? "올바른 숫자가 아닙니다. 1.0에서 5.0 사이 값을 입력하세요."
            : "Invalid number. Enter a value between 1.0 and 5.0.";
    public static string ScaleDialogOutOfRange =>
        _isKorean
            ? "1.0에서 5.0 사이 값만 입력할 수 있습니다."
            : "Value must be between 1.0 and 5.0.";

    // ================================================================
    // 트레이 툴팁
    // ================================================================

    public static string TooltipHangul => _isKorean ? "한글 모드" : "Hangul Mode";
    public static string TooltipEnglish => _isKorean ? "영문 모드" : "English Mode";
    public static string TooltipNonKorean => _isKorean ? "영문 모드 (비한국어)" : "English (Non-Korean)";

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
