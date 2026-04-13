namespace KoEnVue.Utils;

/// <summary>
/// 색상 변환 유틸리티.
/// P4 원칙: HEX -> COLORREF 변환은 이 1곳에서만 구현.
/// config.json 색상 문자열을 Win32 GDI가 요구하는 COLORREF 형식으로 변환한다.
/// </summary>
internal static class ColorHelper
{
    /// <summary>
    /// HEX 문자열 (#RRGGBB 또는 RRGGBB)을 Win32 COLORREF (0x00BBGGRR)로 변환한다.
    /// COLORREF는 BGR 순서임에 주의.
    /// </summary>
    /// <param name="hex">색상 문자열. 예: "#16A34A", "D97706"</param>
    /// <returns>COLORREF 값 (0x00BBGGRR)</returns>
    public static uint HexToColorRef(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6) return 0; // 잘못된 형식 -> 검정

        byte r = byte.Parse(span[0..2], System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(span[2..4], System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(span[4..6], System.Globalization.NumberStyles.HexNumber);

        // COLORREF = 0x00BBGGRR
        return (uint)((b << 16) | (g << 8) | r);
    }

    /// <summary>
    /// HEX 문자열을 (R, G, B) 튜플로 파싱.
    /// premultiplied alpha 처리 등에서 개별 채널이 필요할 때 사용.
    /// </summary>
    public static (byte R, byte G, byte B) HexToRgb(string hex)
    {
        ReadOnlySpan<char> span = hex.AsSpan();
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        if (span.Length != 6) return (0, 0, 0);

        byte r = byte.Parse(span[0..2], System.Globalization.NumberStyles.HexNumber);
        byte g = byte.Parse(span[2..4], System.Globalization.NumberStyles.HexNumber);
        byte b = byte.Parse(span[4..6], System.Globalization.NumberStyles.HexNumber);

        return (r, g, b);
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
