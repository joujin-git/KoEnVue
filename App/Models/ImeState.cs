namespace KoEnVue.App.Models;

/// <summary>
/// IME 감지 결과 3가지 상태.
/// 문자열 비교 금지 -- 반드시 enum으로 비교(P3).
/// </summary>
internal enum ImeState
{
    /// <summary>한국어 IME 활성 + 한글 모드</summary>
    Hangul,

    /// <summary>한국어 IME 활성 + 영문 모드</summary>
    English,

    /// <summary>한국어 IME 비활성 (영문 키보드 등)</summary>
    NonKorean
}
