namespace KoEnVue.Core.Color;

/// <summary>
/// 색상 변환 유틸리티.
/// P4 원칙: HEX -> COLORREF 변환은 이 1곳에서만 구현.
/// config.json 색상 문자열을 Win32 GDI가 요구하는 COLORREF 형식으로 변환한다.
/// </summary>
internal static class ColorHelper
{
    /// <summary>
    /// HEX 문자열 (#RRGGBB 또는 RRGGBB)을 Win32 COLORREF (0x00BBGGRR)로 변환한다.
    /// COLORREF는 BGR 순서임에 주의. 잘못된 형식(길이 불일치, 비 16진 문자)은 검정(0) 반환.
    /// </summary>
    /// <param name="hex">색상 문자열. 예: "#16A34A", "D97706"</param>
    /// <returns>COLORREF 값 (0x00BBGGRR)</returns>
    public static uint HexToColorRef(string hex)
    {
        if (!TryParseHexRgb(hex, out byte r, out byte g, out byte b))
            return 0;

        // COLORREF = 0x00BBGGRR
        return (uint)((b << 16) | (g << 8) | r);
    }

    /// <summary>
    /// HEX 문자열을 (R, G, B) 튜플로 파싱. 잘못된 형식은 (0, 0, 0) 반환.
    /// premultiplied alpha 처리 등에서 개별 채널이 필요할 때 사용.
    /// </summary>
    public static (byte R, byte G, byte B) HexToRgb(string hex)
    {
        if (!TryParseHexRgb(hex, out byte r, out byte g, out byte b))
            return (0, 0, 0);

        return (r, g, b);
    }

    // config.json은 사용자 편집 가능한 시스템 경계이므로 잘못된 16진 문자열(예: "#GGHHII")이
    // 들어올 수 있다. byte.Parse는 FormatException을 던져 GDI 리소스 생성 후의 렌더 경로에서
    // 핸들 누수를 유발하므로 TryParse로 경계 방어.
    private static bool TryParseHexRgb(string hex, out byte r, out byte g, out byte b)
    {
        r = 0; g = 0; b = 0;
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];
        if (span.Length != 6) return false;

        const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
        return byte.TryParse(span[0..2], Hex, null, out r)
            && byte.TryParse(span[2..4], Hex, null, out g)
            && byte.TryParse(span[4..6], Hex, null, out b);
    }

    /// <summary>
    /// COLORREF (0x00BBGGRR)에서 RGB 채널 추출.
    /// GetSysColor 등 Win32 API 반환값을 개별 채널로 분리할 때 사용.
    /// </summary>
    public static (byte R, byte G, byte B) ColorRefToRgb(uint colorRef)
    {
        byte r = (byte)(colorRef & 0xFF);
        byte g = (byte)((colorRef >> 8) & 0xFF);
        byte b = (byte)((colorRef >> 16) & 0xFF);
        return (r, g, b);
    }

    /// <summary>
    /// RGB 바이트를 "#RRGGBB" HEX 문자열로 변환.
    /// </summary>
    public static string RgbToHex(byte r, byte g, byte b)
        => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// 입력된 16진 색상 문자열을 "#RRGGBB" 형식으로 정규화한다.
    /// "#RRGGBB", "RRGGBB" 모두 허용, 결과는 대문자 + # 프리픽스.
    /// </summary>
    public static bool TryNormalizeHex(string input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim();
        if (s.Length == 7 && s[0] == '#') s = s[1..];
        else if (s.Length != 6) return false;
        foreach (char c in s)
        {
            bool isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        normalized = "#" + s.ToUpperInvariant();
        return true;
    }
}
