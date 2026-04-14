# Stage 4 A0 — OverlayStyle 필드 감사

> Stage 5 스모크 완료 후 삭제 예정. Plan A 에이전트 입력용.
>
> 감사 대상: `e:\dev\KoEnVue\App\UI\Overlay.cs`
> 감사 메서드: `EnsureResources` / `EnsureFont` / `CalculateFixedLabelWidth` / `RenderIndicator` / `GetLabelText`
> 1-depth 헬퍼 확장: `RenderLabelText` / `GetBgHex` / `GetFgHex` (RenderIndicator가 호출하는 동일 파일 내 private static)

---

## ⚠️ starter set과의 차이 (요약)

`docs/reorg/05-stage4-core-extraction.md`의 starter set:

```
FontFamily, FontSizePx, Weight, BorderRadiusPx, BorderWidthPx,
PaddingXPx, BgHex, FgHex, BorderHex, Opacity, LabelText
```

**누락된 필드**:
1. `LabelWidthPx` — `EnsureResources`/`CalculateFixedLabelWidth`가 베이스 너비/최소 너비로 직접 사용. 엔진 내부에서 텍스트 폭 측정 후 `Math.Max`로 클램프하는 공식이 살아 있어야 라벨 flip-flop fix가 동작.
2. `LabelHeightPx` — `EnsureResources`/`CalculateFixedLabelWidth`가 DIB 높이를 결정. starter set이 "엔진이 폰트 메트릭에서 계산"한다고 가정했으나 실제 코드는 config 값을 그대로 쓰고 있음.
3. `AltLabelTextsForWidth` (또는 동급 메커니즘) — `CalculateFixedLabelWidth`가 **현재 state와 무관하게** Hangul/English/NonKorean **3개 라벨 모두**의 텍스트 폭을 측정해 가장 넓은 폭으로 고정. starter set의 `LabelText` 단일 필드만으로는 다른 두 라벨의 폭을 알 길이 없어 flip-flop이 재현된다. → **버킷 A에 `MeasureLabels: IReadOnlyList<string>` 추가 필요** (또는 BuildStyle이 항상 3종 라벨을 모두 포함하는 별도 필드를 합성).
4. `Opacity` — RenderIndicator/GetLabelText 5개 메서드 어디에도 등장하지 않음. UpdateLayeredWindow 알파는 `_lastAlpha`로 별도 관리되며, `UpdateOverlay`가 `byte alpha` 인자로 받아 직접 처리. **starter set이 OverlayStyle에 Opacity를 넣은 것은 과잉**이며, 본 5개 메서드 감사 범위에서는 OverlayStyle에서 빼는 것이 정확. (단, Animation/UpdateAlpha 경로의 별도 결정이며 본 감사 권한 밖이라 사실만 기록.)
5. `PaddingXPx` — starter set에 포함됨. 실제 사용 위치는 `CalculateFixedLabelWidth` 1곳뿐이며 값 출처가 `DefaultConfig.LABEL_PADDING_X` 상수(4)임. config 필드가 아님 → 파사드가 합성해 OverlayStyle에 넣어야 함.

**유지되는 starter set 필드**: FontFamily, FontSizePx, Weight, BorderRadiusPx, BorderWidthPx, BgHex, FgHex, BorderHex, LabelText (전부 사용 확인).

**핵심 결론**: starter set은 BorderWidth/Border/Bg/Fg/Font 5종 + LabelText만 추출된 "축소판"이다. 실제 5개 메서드는 **DIB 사이징(Width/Height)** + **3종 라벨 폭 측정** + **상수 패딩** 계산을 모두 엔진이 수행한다. Plan A는 이 3개 추가 항목 없이 설계하면 flip-flop fix가 깨지고 라벨 폭 고정이 동작하지 않는다.

---

## 1. EnsureResources 필드 참조

(AppConfig):
- `config.IndicatorScale` — line 575 (`scale = config.IndicatorScale`)
- `config.LabelWidth` — line 576 (`_baseWidth = (int)Math.Round(config.LabelWidth * scale)`)
- `config.LabelHeight` — line 577 (`_baseHeight = (int)Math.Round(config.LabelHeight * scale)`)

(ImeState): (없음)

(기타):
- `_currentDpiScale` — line 579-580 (`DpiHelper.Scale(_baseWidth, _currentDpiScale)`) — **엔진 내부 캐시 (제외)**
- 1-depth 호출: `EnsureFont(config)` (line 582), `CalculateFixedLabelWidth(config)` (line 588) — 아래 별도 항목 참조
- 호출: `EnsureDib(targetW, targetH)` (line 587) — DIB 생성 헬퍼이며 config/state 미사용

## 2. EnsureFont 필드 참조

(AppConfig):
- `config.FontSize` — line 594 (`scaledFontSize = (int)Math.Round(config.FontSize * config.IndicatorScale)`)
- `config.IndicatorScale` — line 594 (동일 라인 곱셈)
- `config.FontFamily` — line 596 (캐시 키 비교), line 614 (`Gdi32.CreateFontW` 마지막 인자), line 617 (캐시 갱신)
- `config.FontWeight` — line 598 (캐시 키 비교), line 607 (`config.FontWeight == FontWeight.Bold ? FW_BOLD : FW_NORMAL`), line 619 (캐시 갱신)

(ImeState): (없음)

(기타):
- `_currentDpiScale` — line 599 (캐시 키 비교) — **엔진 내부 캐시 (제외)**
- `_currentDpiY` — line 604 (`Kernel32.MulDiv(scaledFontSize, (int)_currentDpiY, 72)`) — **엔진 내부 캐시 (제외)**
- `_cachedFontFamily` / `_cachedFontSize` / `_cachedFontWeight` / `_cachedFontDpiScale` — 모두 캐시 키 (엔진 내부)

## 3. CalculateFixedLabelWidth 필드 참조

(AppConfig):
- `config.HangulLabel` — line 660 (3종 라벨 배열 1번째)
- `config.EnglishLabel` — line 660 (3종 라벨 배열 2번째)
- `config.NonKoreanLabel` — line 660 (3종 라벨 배열 3번째)
- `config.IndicatorScale` — line 669 (`scale = config.IndicatorScale`)
- `config.LabelWidth` — line 672 (최소 너비 클램프 `minWidth`)
- `config.LabelHeight` — line 677 (DIB 재생성 시 높이)

(ImeState): (없음 — **state와 무관하게 3종 라벨 모두 측정**)

(기타):
- `DefaultConfig.LABEL_PADDING_X` — line 670 (`const int LABEL_PADDING_X = 4`, App/Config/DefaultConfig.cs:16) — config record가 아닌 상수
- `_currentDpiScale` — line 670, 672, 677 (DpiHelper.Scale 인자) — **엔진 내부 캐시 (제외)**
- `_currentFont` — line 655, 657 (텍스트 측정용 폰트 핸들) — 엔진 GDI 리소스
- `_memDC` — line 657, 663, 667 — 엔진 GDI 리소스
- 호출: `EnsureDib(_fixedLabelWidth, labelH)` (line 678) — DIB 재생성

## 4. RenderIndicator 필드 참조

직접:

(AppConfig):
- `config.IndicatorScale` — line 710 (`scale = config.IndicatorScale`)
- `config.LabelBorderRadius` — line 711 (`radius`), line 727 (`borderRadius` — 동일 값을 보더 패스에서 다시 계산)
- `config.BorderWidth` — line 716 (`borderW`)
- `config.BorderColor` — line 719 (`ColorHelper.HexToColorRef(config.BorderColor)`)

(ImeState): 없음 (state는 `RenderIndicator` 시그니처로 받지만 직접 비교 없음, 헬퍼 3종에 전달)

(기타): `_currentDpiScale`, `_memDC`, `_nullPen`, `_currentBitmap`, `_ppvBits`, `_lastRenderedState` — **엔진 내부 (제외)**

1-depth 헬퍼 확장 (RenderIndicator가 호출):

### 4-a. `GetBgHex(state, config)` (line 705 호출, 정의 822-827)
- (AppConfig): `config.HangulBg` / `config.EnglishBg` / `config.NonKoreanBg` (state-routed)
- (ImeState): `state == ImeState.Hangul → HangulBg`, `state == ImeState.English → EnglishBg`, `_ → NonKoreanBg`

### 4-b. `RenderLabelText(state, config, w, h)` (line 736 호출, 정의 746-763)
- 직접 (AppConfig): 없음 (config는 헬퍼 2종에만 전달)
- 직접 (ImeState): 없음 (state는 헬퍼 2종에만 전달)
- 호출: `GetFgHex(state, config)` (line 752), `GetLabelText(state, config)` (line 755)
- 기타: `_currentFont`, `_memDC` — 엔진 내부

### 4-c. `GetFgHex(state, config)` (line 752 호출, 정의 829-834)
- (AppConfig): `config.HangulFg` / `config.EnglishFg` / `config.NonKoreanFg` (state-routed)
- (ImeState): `state == ImeState.Hangul → HangulFg`, `state == ImeState.English → EnglishFg`, `_ → NonKoreanFg`

### 4-d. `GetLabelText(state, config)` (line 755 호출 — 5번 항목 참조)

**RenderIndicator + 1-depth 헬퍼 합산 config 필드**:
`IndicatorScale`, `LabelBorderRadius`, `BorderWidth`, `BorderColor`, `HangulBg`, `EnglishBg`, `NonKoreanBg`, `HangulFg`, `EnglishFg`, `NonKoreanFg`, `HangulLabel`, `EnglishLabel`, `NonKoreanLabel` — **총 13개**.

## 5. GetLabelText 필드 참조

(AppConfig):
- `config.HangulLabel` — line 838 (state-routed)
- `config.EnglishLabel` — line 839 (state-routed)
- `config.NonKoreanLabel` — line 840 (state-routed)

(ImeState):
- `state == ImeState.Hangul → HangulLabel`
- `state == ImeState.English → EnglishLabel`
- `_ → NonKoreanLabel` (NonKorean 및 fallback)

(기타): 없음

---

## 5개 메서드 합산 — config 필드 전수 (중복 제거)

| # | config 필드 | 타입 | 출현 메서드 |
|---|---|---|---|
| 1 | `IndicatorScale` | double | EnsureResources, EnsureFont, CalculateFixedLabelWidth, RenderIndicator |
| 2 | `LabelWidth` | int | EnsureResources, CalculateFixedLabelWidth |
| 3 | `LabelHeight` | int | EnsureResources, CalculateFixedLabelWidth |
| 4 | `FontSize` | int | EnsureFont |
| 5 | `FontFamily` | string | EnsureFont |
| 6 | `FontWeight` | FontWeight | EnsureFont |
| 7 | `LabelBorderRadius` | int | RenderIndicator (배경 + 보더 2회) |
| 8 | `BorderWidth` | int | RenderIndicator |
| 9 | `BorderColor` | string (hex) | RenderIndicator |
| 10 | `HangulBg` | string (hex) | RenderIndicator (GetBgHex) |
| 11 | `EnglishBg` | string (hex) | RenderIndicator (GetBgHex) |
| 12 | `NonKoreanBg` | string (hex) | RenderIndicator (GetBgHex) |
| 13 | `HangulFg` | string (hex) | RenderIndicator (RenderLabelText/GetFgHex) |
| 14 | `EnglishFg` | string (hex) | RenderIndicator (RenderLabelText/GetFgHex) |
| 15 | `NonKoreanFg` | string (hex) | RenderIndicator (RenderLabelText/GetFgHex) |
| 16 | `HangulLabel` | string | CalculateFixedLabelWidth (3종 폭 측정), RenderIndicator (GetLabelText), GetLabelText |
| 17 | `EnglishLabel` | string | CalculateFixedLabelWidth (3종 폭 측정), RenderIndicator (GetLabelText), GetLabelText |
| 18 | `NonKoreanLabel` | string | CalculateFixedLabelWidth (3종 폭 측정), RenderIndicator (GetLabelText), GetLabelText |

**총 18개 config 필드**.

또한 config가 아닌 상수 1개:
- `DefaultConfig.LABEL_PADDING_X = 4` (CalculateFixedLabelWidth 사용)

---

## 버킷 A — OverlayStyle 필드로 승격 (엔진이 직접 읽어야 함)

| OverlayStyle 필드 | 출처 | 타입 | 비고 |
|---|---|---|---|
| `FontFamily` | `config.FontFamily` | `string` | 캐시 키 + `CreateFontW` 인자. 그대로 통과. |
| `Weight` | `config.FontWeight` | `FontWeight` | enum. `FW_BOLD`/`FW_NORMAL` 분기는 엔진 내부에서 수행. **주의**: `FontWeight`는 현재 `App/Models/`에 있음 → Stage 4-A에서 `Core/Windowing/`로 이동하거나 `OverlayStyle`이 `bool IsBold`로 변환해야 ImeState 누출 금지 룰과 동일한 "Core가 App 모델 import 금지" 룰을 지킴. **Plan A 결정 필요**. |
| `FontSizePx` | (계산) | `int` | 버킷 B 참조. |
| `LabelWidthPx` | (계산) | `int` | **starter set 누락**. `EnsureResources` 베이스 폭 + `CalculateFixedLabelWidth` 최소 너비 클램프 양쪽에서 사용. 버킷 B 참조. |
| `LabelHeightPx` | (계산) | `int` | **starter set 누락**. DIB 높이 결정. 버킷 B 참조. |
| `BorderRadiusPx` | (계산) | `int` | RenderIndicator의 배경+보더 RoundRect 양쪽에서 동일 값 사용. 버킷 B 참조. |
| `BorderWidthPx` | (계산) | `int` | 0이면 보더 패스 스킵. 버킷 B 참조. |
| `PaddingXPx` | (계산) | `int` | 출처가 `DefaultConfig.LABEL_PADDING_X` 상수(4)이지만 IndicatorScale + DPI 곱셈을 거치므로 사전 계산 필요. 버킷 B 참조. |
| `BgHex` | (계산: state-routed) | `string` | 버킷 C 참조. |
| `FgHex` | (계산: state-routed) | `string` | 버킷 C 참조. |
| `BorderHex` | `config.BorderColor` | `string` | 그대로 통과. (state-independent) |
| `LabelText` | (계산: state-routed) | `string` | 버킷 C 참조. |
| `MeasureLabels` | (계산) | `IReadOnlyList<string>` 또는 `(string Hangul, string English, string NonKorean)` | **starter set 누락 — 필수**. `CalculateFixedLabelWidth`가 flip-flop fix를 위해 state와 무관하게 3종 라벨 모두 측정. starter set의 단일 `LabelText`만 받으면 라벨 폭이 state별로 들쭉날쭉 → 인디케이터가 글자 수에 따라 좁아졌다 넓어졌다 깜빡임. 후보 표현: `IReadOnlyList<string> LabelMeasureSet` (record struct + 길이 3 배열) 또는 명명된 튜플. **Plan A 결정 필요** — 단, 누락 자체는 결정사항 아닌 사실. |

총 13개 OverlayStyle 필드 (starter set 11개에 `LabelWidthPx`/`LabelHeightPx`/`MeasureLabels` 3개 추가, `Opacity` 1개 제거 → 순 +2).

---

## 버킷 B — 파사드에서 사전 계산해 OverlayStyle에 합성

| OverlayStyle 필드 | 계산식 | 비고 |
|---|---|---|
| `FontSizePx` | `(int)Math.Round(config.FontSize * config.IndicatorScale)` | **DPI 미적용**. EnsureFont 코드(line 594)가 IndicatorScale만 곱한 값을 캐시 키로 사용하고, DPI 적용은 `Kernel32.MulDiv(scaledFontSize, (int)_currentDpiY, 72)`로 별도 수행. → **DPI 곱셈은 엔진 소유**가 되어야 EnsureFont의 MulDiv 정밀도(96 DPI 기준 logical pt)가 보존됨. starter set의 "FontSizePx — IndicatorScale + DPI 사전 적용 완료" 코멘트는 **부정확**. |
| `LabelWidthPx` | `DpiHelper.Scale((int)Math.Round(config.LabelWidth * config.IndicatorScale), dpiScale)` | EnsureResources line 576+579 동일 패턴. **DPI 곱셈 필요**. |
| `LabelHeightPx` | `DpiHelper.Scale((int)Math.Round(config.LabelHeight * config.IndicatorScale), dpiScale)` | EnsureResources line 577+580 동일 패턴. **DPI 곱셈 필요**. |
| `BorderRadiusPx` | `DpiHelper.Scale((int)Math.Round(config.LabelBorderRadius * config.IndicatorScale), dpiScale)` | RenderIndicator line 711, 727 동일 패턴. **DPI 곱셈 필요**. |
| `BorderWidthPx` | `DpiHelper.Scale((int)Math.Round(config.BorderWidth * config.IndicatorScale), dpiScale)` | RenderIndicator line 716. **DPI 곱셈 필요**. |
| `PaddingXPx` | `DpiHelper.Scale((int)Math.Round(DefaultConfig.LABEL_PADDING_X * config.IndicatorScale), dpiScale)` | CalculateFixedLabelWidth line 670. 출처가 `DefaultConfig.LABEL_PADDING_X` 상수이므로 파사드가 들고 와야 함 → Core 엔진은 `DefaultConfig` 모름. **DPI 곱셈 필요**. |

**모든 6개 픽셀 필드는 DPI 곱셈을 거친 최종 값**이어야 한다는 것이 일관된 패턴.

### ⭐ DPI 곱셈 소유권 결정 (Plan A의 핵심 의사결정점)

**선택지 1: 파사드가 DPI까지 모두 곱한 최종 px를 OverlayStyle에 넣고 엔진은 DPI 모름**
- 장점:
  - OverlayStyle 1개로 렌더 완전 결정. 엔진은 `int FontSizePx`만 받아 `CreateFontW`에 넣으면 끝.
  - DPI가 변하면 파사드가 새 OverlayStyle을 만들어 `Render(style)` 재호출 → 엔진은 그냥 다시 그림.
  - 엔진이 DpiHelper에 의존하지 않아도 됨 → Core/Windowing의 의존성 그래프가 가장 평평.
- 단점:
  - **EnsureFont의 MulDiv 정밀도 손실 위험**. 현재 코드는 `scaledFontSize = round(FontSize * IndicatorScale)` (정수 픽셀)을 캐시 키로 쓰고, 그 정수에 `MulDiv(_, dpiY, 72)`를 적용해 폰트 픽셀 높이를 구한다. MulDiv는 (a*b/c)를 64bit로 계산해 마지막에 32bit로 자르는 방식이라 `(int)Math.Round(FontSize * IndicatorScale * dpiScale)` 같은 단순 곱셈보다 정밀도가 높다. 폰트 사이즈 12 + scale 1.0 + 144 dpi에서 차이는 0~1px 수준이지만, 텍스트 안티앨리어싱과 라벨 폭 계산이 1px 단위로 영향을 받는다.
  - DPI 변경 시 파사드가 6개 필드를 모두 다시 계산해야 함.
  - 파사드가 DPI를 알아야 함 → DpiHelper 의존성을 파사드도 가짐 (현재도 Overlay.cs가 이미 사용 중이라 신규 의존성은 아님).

**선택지 2: 파사드가 IndicatorScale만 곱하고 엔진이 DPI를 곱함**
- 장점:
  - **EnsureFont의 MulDiv 정밀도 보존**. 엔진 내부에서 `scaledFontSize` (IndicatorScale만 곱한 정수) → MulDiv → 픽셀 높이의 흐름이 그대로 유지됨.
  - 엔진이 DPI 변경을 자체적으로 감지/캐시 가능 (`_currentDpiScale`, `_currentDpiY` 필드를 그대로 가져감).
  - 파사드 BuildStyle 로직이 단순: `(int)Math.Round(config.X * config.IndicatorScale)` 5종 + 폰트.
- 단점:
  - OverlayStyle 필드 이름이 `LabelWidthPx`이지만 사실은 "DPI 미적용 px" — 명명이 헷갈림. → `LabelWidthLogical` / `BaseLabelWidth` 같은 이름이 더 정확하지만 starter set 명명과 충돌.
  - 엔진이 `DpiHelper`(Core/Dpi)에 의존 → Core/Windowing이 Core/Dpi를 import. 같은 Core 내부라 P6 위반은 아니지만 Core 내 의존성 그래프가 한 단계 깊어짐.
  - DPI 변경 시 엔진이 DpiHelper.GetScale을 직접 호출해야 함 → 엔진이 hwnd로 DPI를 조회하는 책임을 가짐 (현재 코드와 동일).

**감사 결과의 사실 기반 권고 (결정은 Plan A 권한)**:
- EnsureFont의 MulDiv 정밀도 손실은 **실측 가능한 회귀 위험**이다. 이 단일 사실이 선택지 2를 강하게 가리킨다.
- 그러나 starter set 코멘트("FontSizePx — IndicatorScale + DPI 사전 적용 완료")는 명시적으로 선택지 1을 가정한다.
- **두 선택지의 명명 일관성을 위한 절충안**: OverlayStyle은 `FontSizePx` 한 필드만 "IndicatorScale 적용된 logical px (DPI 미적용)"로 정의하고, 엔진이 DPI를 곱한다. 다른 5개 픽셀 필드(`LabelWidthPx`/`LabelHeightPx`/`BorderRadiusPx`/`BorderWidthPx`/`PaddingXPx`)도 같은 정의로 통일. **이 경우 이름은 `Px`가 아니라 `LogicalPx` 또는 `Base`로 변경**하는 것이 정확. starter set은 명명을 다시 검토할 것.

---

## 버킷 C — 상태 라우팅 (파사드 BuildStyle에서만 수행)

| OverlayStyle 필드 | 분기 로직 |
|---|---|
| `LabelText` | `state == ImeState.Hangul ? config.HangulLabel : state == ImeState.English ? config.EnglishLabel : config.NonKoreanLabel` |
| `BgHex` | `state == ImeState.Hangul ? config.HangulBg : state == ImeState.English ? config.EnglishBg : config.NonKoreanBg` |
| `FgHex` | `state == ImeState.Hangul ? config.HangulFg : state == ImeState.English ? config.EnglishFg : config.NonKoreanFg` |
| `MeasureLabels` (3종 동시) | `[config.HangulLabel, config.EnglishLabel, config.NonKoreanLabel]` — state와 무관, 항상 3개 모두 |

**ImeState 누출 금지 조건**: 위 4개 필드만 파사드 `BuildStyle(config, state)` 내부에서 state→string 변환을 수행하고, 엔진은 `OverlayStyle`의 string 필드만 받음. `RenderIndicator(ImeState state, AppConfig config)` 시그니처가 `Render(OverlayStyle style)`로 변환되면서 `ImeState`는 파사드 경계를 넘지 않음. → 스펙(`docs/reorg/05-stage4-core-extraction.md` line 105) 충족.

**`MeasureLabels` 특이점**: state-routed가 아니라 **state-independent** 합성. starter set이 라벨 측정용 부가 필드를 누락한 결과 버킷 분류가 부자연스러워졌으나, 동일 record에 함께 들어가는 것이 가장 자연스럽다 (별도 record로 분리하면 BuildStyle이 2개 객체를 반환해야 함).

---

## 비고

### 1. IndicatorScale + DPI 곱셈 소유권 — 파사드 vs 엔진

상기 "버킷 B의 ⭐ 결정점" 참조. 핵심 사실:
- 현재 코드의 EnsureFont는 `MulDiv`를 사용해 DPI 곱셈 정밀도를 명시적으로 챙긴다.
- starter set의 "FontSizePx — DPI 사전 적용 완료" 명세를 그대로 따르면 MulDiv 경로가 단순 곱셈으로 전락한다.
- 엔진이 DPI를 곱하는 쪽이 회귀 위험이 낮다 (사실 기반 권고, Plan A 권한 결정).

### 2. ImeState가 Core에 누출되지 않도록 옮길 분기

- `RenderIndicator`의 1번째 인자 `ImeState state`를 제거하고 `OverlayStyle style`만 받도록 변경.
- `RenderIndicator`가 호출하는 `GetBgHex` / `GetFgHex` / `GetLabelText` / `RenderLabelText` 4개 헬퍼는 모두 `state` 파라미터를 사용하므로, 이들도 변경 필요:
  - `GetBgHex(state, config)` → 사라짐. 파사드 BuildStyle이 미리 `BgHex`를 합성해 OverlayStyle에 넣음.
  - `GetFgHex(state, config)` → 사라짐. 동일.
  - `GetLabelText(state, config)` → 사라짐. 동일.
  - `RenderLabelText(state, config, w, h)` → `RenderLabelText(OverlayStyle style, int w, int h)`. 내부에서 `style.FgHex` + `style.LabelText` 사용.
- `_lastRenderedState` 타입 `ImeState?` → `OverlayStyle?` 변경 (스펙 line 66 확인). flip-flop 가드는 `OverlayStyle.Equals(prev, curr)` 비교로 대체. **단**, OverlayStyle을 `record struct`로 선언하면 자동 값 동등성이 string 필드들 ord 비교까지 포함해 작동함.
- ImeState 변환 지점: `Overlay.BuildStyle(config, state)` **단 1곳**. 호출처는 `Show(int, int, ImeState)` / `UpdateColor(ImeState)` 등 **파사드의 public 메서드들** 내부에서만 발생.

### 3. RenderIndicator가 단계별로 읽는 config 필드 (시각적 트레이스)

| 단계 | RenderIndicator 라인 | 읽는 config 필드 |
|---|---|---|
| 1. 픽셀 버퍼 클리어 | 696-699 | (없음) |
| 2. NULL_PEN 선택 | 702 | (없음) |
| 3. 배경색 브러시 | 705-706 | `HangulBg`/`EnglishBg`/`NonKoreanBg` (state-routed) |
| 4. RoundedRect 배경 | 709-713 | `IndicatorScale`, `LabelBorderRadius` |
| 5. 테두리 (있을 때) | 716-733 | `IndicatorScale`(중복), `BorderWidth`, `BorderColor`, `LabelBorderRadius`(중복) |
| 6. 텍스트 렌더링 | 736 → RenderLabelText | (RenderLabelText: `HangulFg`/`EnglishFg`/`NonKoreanFg` (state-routed), `HangulLabel`/`EnglishLabel`/`NonKoreanLabel` (state-routed)) |
| 7. 정리 | 739-740 | (없음) |
| 8. Premultiplied alpha | 743 → ApplyPremultipliedAlpha | (없음 — 픽셀만 후처리) |

총 7개 unique config 필드 (RenderIndicator 본체) + 6개 (RenderLabelText 1-depth) = **13개 필드**. starter set의 BgHex/FgHex/BorderHex/BorderRadiusPx/BorderWidthPx/LabelText/FontFamily/FontSizePx/Weight 9개 + 본 감사가 추가한 LabelWidthPx/LabelHeightPx/PaddingXPx/MeasureLabels 4개 = 13개 OverlayStyle 필드 ↔ 5 메서드 합산 18개 config 필드 ↔ 1개 상수.

### 4. CalculateFixedLabelWidth의 3종 라벨 동시 측정 — 설계 핵심

`CLAUDE.md`의 키 결정사항 "Label DIB flip-flop fix"는 다음 동작을 명시한다:
> Cache `_fixedLabelWidth` and invalidate `_lastRenderedState` when DIB is recreated.

이 캐시는 `CalculateFixedLabelWidth`가 한 번에 3종 라벨 폭을 모두 측정해 가장 넓은 폭으로 고정해야만 의미가 있다. 만약 OverlayStyle이 단일 `LabelText`만 보낸다면:
- BuildStyle이 호출될 때마다 다른 라벨이 들어옴
- 엔진이 매번 그 라벨만 측정 → `_fixedLabelWidth`가 state 전환 시마다 변동 → DIB 재생성 → 화면 깜빡임

→ **OverlayStyle에 3종 측정 라벨 세트가 반드시 포함**되어야 함. 이름은 Plan A 권한이지만, 사실은 협상 불가.

### 5. FontWeight enum의 Core/App 경계 문제

`config.FontWeight` 필드는 `App/Models/`의 `FontWeight` enum (`Normal`/`Bold` 2종 추정). OverlayStyle이 `FontWeight Weight`를 가지면 Core/Windowing이 App/Models를 import해야 함 → **P6 1-방향 의존성 위반**.

해결안 후보 (Plan A 결정):
- (a) FontWeight enum을 `Core/Windowing/`로 이동. App에서는 `using KoEnVue.Core.Windowing;`로 참조.
- (b) OverlayStyle은 `bool IsBold`만 보유. BuildStyle이 `config.FontWeight == FontWeight.Bold` 변환 수행. Normal/Bold 외 FontWeight 값이 향후 추가될 가능성에 대한 판단 필요.
- (c) Core/Windowing 내에 별도 `OverlayFontWeight` enum 생성. App→Core 변환은 BuildStyle이 처리. 명명 중복.

본 감사는 사실만 기록: **`config.FontWeight`는 App/Models 의존성을 OverlayStyle에 끌어들인다**.

---

## 최종 권고 OverlayStyle 필드 리스트 (사실 기반)

```csharp
public readonly record struct OverlayStyle(
    // 폰트
    string FontFamily,
    int FontSizeLogicalPx,        // config.FontSize * IndicatorScale (DPI 미적용; 엔진이 MulDiv로 곱함)
    /* FontWeight 또는 bool */ Weight,

    // 사이즈 (DPI 미적용 또는 적용 — Plan A가 결정)
    int LabelWidthLogicalPx,      // config.LabelWidth * IndicatorScale  ← starter set 누락
    int LabelHeightLogicalPx,     // config.LabelHeight * IndicatorScale ← starter set 누락
    int BorderRadiusLogicalPx,    // config.LabelBorderRadius * IndicatorScale
    int BorderWidthLogicalPx,     // config.BorderWidth * IndicatorScale
    int PaddingXLogicalPx,        // DefaultConfig.LABEL_PADDING_X * IndicatorScale (상수 기반)

    // 색상 (state-routed, 파사드가 합성)
    string BgHex,
    string FgHex,
    string BorderHex,             // state-independent, config.BorderColor 그대로

    // 라벨 텍스트
    string LabelText,             // 현재 state의 라벨 (그리기용)

    // 라벨 측정용 (flip-flop fix 필수)
    /* IReadOnlyList<string> 또는 (string,string,string) 튜플 */ MeasureLabels  ← starter set 누락
);
```

**필드 수**: 13개 (starter set 11개 → +3 추가, -1 제거: Opacity).

**Opacity 제거 근거**: 5개 메서드 감사 범위 어디에도 등장하지 않음. 알파 처리는 `UpdateOverlay`/`UpdateLayeredWindow` 경로에서 별도 관리되며 (`_lastAlpha`), `LayeredOverlayBase.UpdateAlpha(byte)`가 별도 메서드로 존재한다는 스펙(line 45). → OverlayStyle의 일부가 아님.
