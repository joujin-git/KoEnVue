namespace KoEnVue.Core.Windowing;

/// <summary>
/// LayeredOverlayBase의 렌더링 입력. 앱 설정 레코드를 Core 경계 밖으로 빼기 위해
/// 파사드가 상태 enum + 설정을 합성해 만드는 값 타입.
/// <para>
/// 모든 "LogicalPx" 필드는 <c>IndicatorScale</c>만 곱해진 상태이며 DPI 스케일링은
/// 엔진이 내부에서 수행한다. 이는 <c>EnsureFont</c>의 <c>Kernel32.MulDiv</c> 정밀도를 보존하기
/// 위함이다 (단순 <c>(int)Math.Round(...)</c>로 대체 시 0~1px 단위 라벨 폭 회귀 가능).
/// </para>
/// <para>
/// <c>record struct</c>로 선언되어 값 동등성 비교가 자동으로 동작한다. 엔진의
/// flip-flop 가드는 이전 스타일과 현재 스타일을 <c>==</c>로 비교해 동일 시 재렌더를 스킵한다.
/// </para>
/// <para>
/// 레이어 경계: 이 타입은 KoEnVue.Core.Windowing 네임스페이스에 있으며 App 레이어
/// 의존성이 없다. 파사드가 상태 enum을 문자열/색상/치수로 해석해 본 레코드에 주입한다.
/// </para>
/// </summary>
internal readonly record struct OverlayStyle(
    // === 폰트 ===
    string FontFamily,
    int FontSizeLogicalPx,      // config.FontSize * IndicatorScale (DPI 미적용)
    bool IsBold,                // 앱 레이어의 굵기 설정을 파사드가 bool로 변환

    // === 사이즈 (IndicatorScale 적용, DPI 미적용 — 엔진이 DpiHelper.Scale 수행) ===
    int LabelWidthLogicalPx,    // config.LabelWidth * IndicatorScale
    int LabelHeightLogicalPx,   // config.LabelHeight * IndicatorScale
    int BorderRadiusLogicalPx,  // config.LabelBorderRadius * IndicatorScale
    int BorderWidthLogicalPx,   // config.BorderWidth * IndicatorScale (0이면 보더 패스 스킵)
    int PaddingXLogicalPx,      // 라벨 가로 패딩 상수 * IndicatorScale (파사드가 합성)

    // === 색상 (state-routed — 파사드가 합성) ===
    string BgHex,               // Hangul/English/NonKoreanBg 중 현재 상태에 해당
    string FgHex,               // Hangul/English/NonKoreanFg 중 현재 상태에 해당
    string BorderHex,           // config.BorderColor (state-independent)

    // === 라벨 텍스트 (state-routed) ===
    string LabelText,           // 현재 상태의 라벨 (그리기용)

    // === CAPS LOCK 표시 (state-independent — 파사드가 시스템 토글 상태를 주입) ===
    // true면 라벨 좌우 세로 막대(fg 색상)가 그려진다. 토글은 BuildStyle이 static 필드에서 읽어와
    // 매 렌더마다 반영하므로 record 동등성 비교 시 state-transition 트리거로도 작동한다.
    bool CapsLockOn,

    // === 라벨 측정용 3종 (flip-flop fix 필수 — state-independent) ===
    // CalculateFixedLabelWidth는 상태와 무관하게 3종 라벨 모두를 측정해 최대 폭으로
    // _fixedLabelWidth를 고정한다. 단일 LabelText만 사용하면 state 전환 시마다 라벨 폭이
    // 변동해 DIB 재생성 → 깜빡임이 발생한다.
    (string Hangul, string English, string NonKorean) MeasureLabels
);

/// <summary>
/// LayeredOverlayBase가 <c>renderToDib</c> 콜백에 전달하는 DPI 적용 후 메트릭.
/// 파사드 렌더 콜백은 본 메트릭의 <c>Scaled*</c> 픽셀 값을 그대로 GDI API에 넘기면 된다.
/// <para>
/// 폰트 핸들은 엔진이 사전 SelectObject한 상태로 전달되므로 콜백은 <c>DrawTextW</c>만
/// 호출하면 된다. 폰트 픽셀 높이(<c>ScaledFontHeightPx</c>)는 참고용이며 이미 HFONT에 반영됨.
/// </para>
/// </summary>
internal readonly record struct OverlayMetrics(
    double DpiScale,
    uint DpiY,
    int ScaledWidth,            // 현재 DIB 물리 픽셀 폭
    int ScaledHeight,           // 현재 DIB 물리 픽셀 높이
    int ScaledPaddingX,         // 텍스트 좌우 패딩 (물리 픽셀)
    int ScaledBorderWidth,      // 보더 두께 (물리 픽셀, 0이면 보더 생략)
    int ScaledBorderRadius,     // 모서리 반경 (물리 픽셀)
    int ScaledFontHeightPx,     // MulDiv로 산출된 음수 폰트 픽셀 높이 (참고용)

    // DT_VCENTER가 폰트 셀(tmAscent+tmDescent의 중점)을 기준으로 정렬하는 탓에 발생하는
    // 시각적 하향 치우침 보정값. 양수일수록 텍스트를 위로 그만큼 끌어올린다(물리 픽셀).
    // 콜백은 textRect의 Top/Bottom을 동시에 -TextVCenterOffsetPx만큼 이동시키면 된다 —
    // 사각형 높이는 보존되므로 DT_VCENTER 자체는 정상 동작하고, 그 안의 셀이 위로 이동한다.
    int TextVCenterOffsetPx
);
