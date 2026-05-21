using System.Text.Json.Serialization;

namespace KoEnVue.App.Models;

/// <summary>
/// UI 표시 언어 — 트레이 메뉴/다이얼로그/툴팁 문자열에 적용된다.
/// config.json 의 <c>"language"</c> 키에 대응.
///
/// <para>
/// Auto = Windows 시스템 UI 언어가 한국어이면 ko, 아니면 en (P2: 기본 한국어 fallback).
/// Ko = 강제 한국어. En = 강제 영어.
/// </para>
///
/// <para>
/// enum 명을 <c>AppLanguage</c> 로 둔 이유: <c>AppConfig</c> 의 property 이름과 동일한
/// <c>Language</c> 로 두면 <c>public Language Language { get; init; }</c> 형태가 되어
/// 가독성이 떨어진다. 기존 string 키 <c>"auto"/"ko"/"en"</c> 는 그대로 호환된다.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AppLanguage>))]
internal enum AppLanguage
{
    /// <summary>시스템 언어 자동 감지 (기본값).</summary>
    [JsonStringEnumMemberName("auto")]
    Auto = 0,

    /// <summary>한국어 강제.</summary>
    [JsonStringEnumMemberName("ko")]
    Ko = 1,

    /// <summary>영어 강제.</summary>
    [JsonStringEnumMemberName("en")]
    En = 2,
}
