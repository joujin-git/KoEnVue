namespace KoEnVue.Core.Xml;

/// <summary>
/// XML 5 predefined entities (<c>&amp;amp;</c> / <c>&amp;lt;</c> / <c>&amp;gt;</c> /
/// <c>&amp;quot;</c> / <c>&amp;apos;</c>) escape / unescape 헬퍼.
/// schtasks XML 조립 + 역파싱처럼 의존성 없이 1.0 spec 만 필요한 경량 케이스용.
/// 본격 XML 처리는 <see cref="System.Xml.XmlReader"/> 등을 사용한다.
/// </summary>
internal static class XmlEntityCodec
{
    /// <summary>
    /// 입력 문자열의 5 entities 를 escape 한다. <c>&amp;</c> 가 가장 먼저 처리되어
    /// 다른 엔티티 안의 <c>&amp;</c> 가 중복 인코딩되는 것을 막는다.
    /// </summary>
    internal static string Escape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");

    /// <summary>
    /// 5 entities 를 복원한다. <c>&amp;amp;</c> 를 마지막에 처리해야 원본 <c>&amp;</c> 가
    /// 다른 엔티티의 앰퍼샌드를 의도치 않게 잡아채는 것을 막을 수 있다.
    /// </summary>
    internal static string Unescape(string s) =>
        s.Replace("&quot;", "\"")
         .Replace("&apos;", "'")
         .Replace("&lt;", "<")
         .Replace("&gt;", ">")
         .Replace("&amp;", "&");
}
