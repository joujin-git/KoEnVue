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
    // 트레이 메뉴 — 인디케이터 스타일
    // ================================================================

    public static string StyleDot => _isKorean ? "점" : "Dot";
    public static string StyleSquare => _isKorean ? "사각" : "Square";
    public static string StyleUnderline => _isKorean ? "밑줄" : "Underline";
    public static string StyleVbar => _isKorean ? "세로바" : "Vertical Bar";
    public static string StyleLabel => _isKorean ? "텍스트" : "Text";

    // ================================================================
    // 트레이 메뉴 — 표시 모드
    // ================================================================

    public static string DisplayEvent => _isKorean ? "이벤트 시만" : "On Event";
    public static string DisplayAlways => _isKorean ? "항상 표시" : "Always";

    // ================================================================
    // 트레이 메뉴 — 투명도
    // ================================================================

    public static string OpacityHigh => _isKorean ? "진하게" : "High";
    public static string OpacityNormal => _isKorean ? "보통" : "Normal";
    public static string OpacityLow => _isKorean ? "연하게" : "Low";

    // ================================================================
    // 트레이 메뉴 — 메인 메뉴
    // ================================================================

    public static string MenuIndicatorStyle => _isKorean ? "인디케이터 스타일" : "Indicator Style";
    public static string MenuDisplayMode => _isKorean ? "표시 모드" : "Display Mode";
    public static string MenuOpacity => _isKorean ? "투명도" : "Opacity";
    public static string MenuStartup => _isKorean ? "시작 프로그램 등록" : "Start with Windows";
    public static string MenuOpenSettings => _isKorean ? "설정 파일 열기..." : "Open Settings...";
    public static string MenuExit => _isKorean ? "종료" : "Exit";

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
