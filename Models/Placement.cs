namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 배치 방향.
/// Label 스타일: Left -> Above -> Below 자동 전환.
/// 캐럿 박스 스타일: CaretTopRight / CaretBelow / CaretOverlap 고정.
/// </summary>
internal enum Placement
{
    /// <summary>캐럿 왼쪽 (label 기본)</summary>
    Left,

    /// <summary>캐럿 오른쪽 (label 설정 가능)</summary>
    Right,

    /// <summary>캐럿 위 (label fallback)</summary>
    Above,

    /// <summary>캐럿 아래 (label fallback)</summary>
    Below,

    /// <summary>캐럿 오른쪽 상단 (caret_dot, caret_square)</summary>
    CaretTopRight,

    /// <summary>캐럿 바로 아래 (caret_underline)</summary>
    CaretBelow,

    /// <summary>캐럿 위치에 겹침 (caret_vbar)</summary>
    CaretOverlap
}
