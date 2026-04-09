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
    public static string MenuStartup => _isKorean ? "시작 프로그램 등록" : "Start with Windows";
    public static string MenuCleanup => _isKorean ? "미사용 위치 데이터 정리" : "Clean unused position data";
    public static string MenuExit => _isKorean ? "종료" : "Exit";

    // ================================================================
    // 포터블/설치 모드
    // ================================================================

    public static string PortableLabel => _isKorean ? "[포터블]" : "[Portable]";
    public static string InstalledLabel => _isKorean ? "[설치]" : "[Installed]";

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
