namespace KoEnVue.App.Detector;

/// <summary>
/// IME / 키보드 레이아웃 관련 Win32 상수. <c>Core</c> 레이어는 IME 어휘를 알지
/// 않도록 (P6) App 레이어에 두며, <c>ImeStatus</c> 의 3-tier fallback + WinEvent 콜백
/// + <c>I18n</c> 의 시스템 로케일 한국어 판정에서 사용된다.
/// </summary>
internal static class ImeConstants
{
    // --- IME 메시지 ---
    /// <summary>WM_IME_CONTROL — IME 윈도우에 명령/조회 전송 (SendMessageTimeoutW).</summary>
    public const uint WM_IME_CONTROL          = 0x0283;
    /// <summary>IMC_GETOPENSTATUS — IME 오픈 여부 조회 wParam.</summary>
    public const uint IMC_GETOPENSTATUS       = 0x0005;
    /// <summary>IMC_GETCONVERSIONMODE — 변환 모드 비트필드 조회 wParam.</summary>
    public const uint IMC_GETCONVERSIONMODE   = 0x0001;
    /// <summary>IME_CMODE_HANGUL — 변환 모드 비트필드의 한글 입력 비트.</summary>
    public const uint IME_CMODE_HANGUL        = 0x01;

    // --- WinEvent — IME 변경 콜백 ---
    /// <summary>EVENT_OBJECT_IME_CHANGE — IME 상태 전환 시 발화 (Windows SDK WinUser.h).</summary>
    public const uint EVENT_OBJECT_IME_CHANGE = 0x8029;

    // --- HKL (Keyboard Layout) 파싱 ---
    /// <summary>LOWORD(HKL) → LANGID. 0x0412 = 한국어.</summary>
    public const ushort LANGID_KOREAN         = 0x0412;
    /// <summary>HKL 의 LOWORD (LANGID) 추출 마스크.</summary>
    public const long   HKL_LANGID_MASK       = 0xFFFF;

    /// <summary>
    /// HIWORD(HKL) 최상위 니블 마스크. IME 디바이스 시그니처(0xE) 확인용.
    /// 0x0409_0409 같은 단순 키보드 레이아웃과 0xE001_0412(한글), 0xE001_0411(일본어 IME) 등을 구분.
    /// </summary>
    public const uint HKL_IME_DEVICE_MASK     = 0xF0000000;
    /// <summary>HIWORD(HKL) 최상위 니블이 IME 디바이스임을 의미하는 시그니처 (0xE).</summary>
    public const uint HKL_IME_DEVICE_SIG      = 0xE0000000;
}
