namespace KoEnVue.Models;

/// <summary>
/// 인디케이터 스타일 5종.
/// config.json의 "indicator_style" 키에 대응.
/// </summary>
internal enum IndicatorStyle
{
    /// <summary>캐럿 옆 텍스트 라벨 ("한"/"En"). 기본 28x24px.</summary>
    Label,

    /// <summary>소형 원형. 기본 8x8px. 기본 스타일.</summary>
    CaretDot,

    /// <summary>소형 사각. 기본 8x8px.</summary>
    CaretSquare,

    /// <summary>얇은 밑줄 바. 기본 24x3px.</summary>
    CaretUnderline,

    /// <summary>얇은 세로 바. 기본 3x16px.</summary>
    CaretVbar
}
