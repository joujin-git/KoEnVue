namespace KoEnVue.Core.Windowing;

/// <summary>
/// 커서 추종 인디케이터 (동심원 3개 + 헤일로) 의 렌더 입력. <see cref="LayeredCursorBase"/>
/// 가 콜백에 전달하는 closed type — 메인 인디 <see cref="OverlayStyle"/> 와 형제 관계이며 별도 엔진 (LayeredCursorBase)
/// 으로 처리된다. P4 ("하나의 구현만") 예외 정당화는 [docs/dev-notes/2026-05-27-cursor-indicator.md](../../docs/dev-notes/2026-05-27-cursor-indicator.md):
/// 메인 인디 알파 race 미해결 영역에 변경면을 추가하지 않기 위해 엔진 분리.
/// <para>
/// 모든 LogicalPx 필드는 사용자 설정 값 (DPI 미적용). 엔진이 <see cref="Core.Dpi.DpiHelper.Scale"/> 로 변환.
/// </para>
/// <para>
/// CAPS LOCK 외곽 원 색상 (<see cref="OuterColorArgb"/>) 은 현재 IME 의 반대 카테고리 색상 — App
/// 측 <c>CursorOverlay.BuildStyle</c> 가 합성. 영문 IME → 한글 색상. 한글/비한글 IME → 영문 색상.
/// </para>
/// </summary>
internal readonly record struct CursorStyle(
    int OuterRadiusLogicalPx,
    int MiddleRadiusLogicalPx,
    int InnerRadiusLogicalPx,
    int CoreThicknessLogicalPx,
    int HaloThicknessLogicalPx,
    double HaloOpacity,
    uint InnerColorArgb,
    uint MiddleColorArgb,
    uint OuterColorArgb,
    bool CapsLockOn,
    double HighlightScale = 1.0
)
{
    /// <summary>
    /// IME 전환 스케일 팝의 최대 배율 = config clamp 상한(App 측 <c>DefaultConfig.MaxCursorHighlightScale</c>)
    /// = bbox 고정 기준. <see cref="BoundingBoxLogicalPx"/> 가 이 배율로 DIB 크기를 고정해, 팝 진행 중
    /// <see cref="HighlightScale"/> 이 1.0→사용자 설정 배율 사이에서 변해도 DIB 재생성이 0 이다.
    /// App const 가 본 Core const 를 참조한다 (단일 진실원, P6 정방향).
    /// </summary>
    public const double MaxHighlightScale = 2.0;
    /// <summary>
    /// DIB 정사각형 한 변 길이 (logical px, DPI 미적용). 외측 반지름 기준 + 헤일로 외측 확장
    /// (헤일로가 코어보다 양옆 (halo - core) / 2 만큼 확장) + AA 여유 1px. CAPS 토글에 무관하게
    /// 항상 외측 원 기준으로 잡아 DIB 재생성 없이 CAPS 토글 가능.
    /// </summary>
    public int BoundingBoxLogicalPx
    {
        get
        {
            int maxRadius = Math.Max(OuterRadiusLogicalPx,
                Math.Max(MiddleRadiusLogicalPx, InnerRadiusLogicalPx));
            int haloOuterExtension = (HaloThicknessLogicalPx - CoreThicknessLogicalPx + 1) / 2;
            if (haloOuterExtension < 0) haloOuterExtension = 0;
            int outsideMargin = CoreThicknessLogicalPx / 2 + haloOuterExtension + 1;
            // 팝 최대 배율(MaxHighlightScale)까지 동심원이 확대돼도 DIB 안에 들어오도록 bbox 를 고정 확대.
            // HighlightScale(매 프레임 변동값) 이 아닌 MaxHighlightScale(상수) 기준이라 팝 중 DIB 재생성 0.
            int halfExtent = (int)Math.Ceiling((maxRadius + outsideMargin) * MaxHighlightScale);
            return halfExtent * 2;
        }
    }
}

/// <summary>
/// <see cref="LayeredCursorBase"/> 가 콜백에 전달하는 DPI 적용 후 메트릭.
/// <see cref="OverlayMetrics"/> 와 형제 — cursor 인디 전용으로 필요한 3개 필드만.
/// </summary>
internal readonly record struct CursorMetrics(
    double DpiScale,
    int ScaledWidth,
    int ScaledHeight
);
