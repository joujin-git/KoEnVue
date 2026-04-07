namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 표시 모드.
/// config.json의 "display_mode" 키에 대응.
/// </summary>
internal enum DisplayMode
{
    /// <summary>이벤트 시에만 표시 (기본값). 페이드인 -> 유지 -> 페이드아웃 -> 숨김.</summary>
    OnEvent,

    /// <summary>항상 표시. 유휴 시 idle_opacity, 활성 시 active_opacity.</summary>
    Always
}
