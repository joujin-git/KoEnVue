# SettingsDialog.BuildChildren 분해 — 레이아웃 메트릭 struct + 윈도우 생성 헬퍼 5종 (2026-06-02, IMP-2)

> **상태**: 완료 (build 0/0 · AOT publish 0/0 · test 82 → 90 PASS · reviewer P1–P6 0위반). 추적 [improvement-plan/AUDIT-2026-06-02-codebase-review.md](../improvement-plan/AUDIT-2026-06-02-codebase-review.md) IMP-2 행 + 묶음 6. 본 dev-note 는 분해 설계 결정 5건 + 픽셀 회귀 수동 육안검증 체크리스트.

## 무엇

[종합 코드 감사](../improvement-plan/AUDIT-2026-06-02-codebase-review.md) IMP-2 (묶음 6). 종전 [`App/UI/Dialogs/SettingsDialog.cs`](../../App/UI/Dialogs/SettingsDialog.cs) 의 `BuildChildren` 은 ~184줄 단일 메서드 — 메트릭 스케일 21변수 + 뷰포트 클래스 등록 + 설명 레이블 + 뷰포트 + 행 루프 3-way switch(Bool/Combo/Int·Double·String·Color) + 스크롤바 + 버튼이 한 메서드에 깊게 중첩돼 있었다. 이를 둘로 갈랐다.

| 산물 | 책임 | LOC |
|------|------|-----|
| 신규 [`SettingsDialog.Layout.cs`](../../App/UI/Dialogs/SettingsDialog.Layout.cs) (4번째 partial) | `internal readonly record struct SettingsLayout`(27필드) + `internal static SettingsLayout BuildLayout(DialogShellContext)` 순수 팩토리 | ~75 |
| [`SettingsDialog.cs`](../../App/UI/Dialogs/SettingsDialog.cs) `BuildChildren` | ~17줄 오케스트레이터 + 윈도우 생성 헬퍼 5종 | ~17 + 5헬퍼 |

`SettingsLayout` 27필드 = 18 DPI-스케일 메트릭(`Pad`/`DescH`/…/`ViewportScrollReserve`) + `ClientW`/`ClientH` + 뷰포트 지오메트리 `VpX`/`VpY`/`VpW`/`VpH` + 파생 `LabelX`/`ControlX`/`SectionContentW`. `BuildLayout` 이 logical 상수를 1회 DPI 스케일하고 파생 좌표(뷰포트 지오메트리·열 위치·clamp 된 컨트롤 폭)를 순수 산술로 계산한다.

윈도우 생성 헬퍼 5종: `RegisterViewportClass()` / `CreateDescriptionLabel(ctx, m)` / `CreateViewport(ctx, m)` → HWND / `LayoutRows(ctx, m, rows)` → totalContentH / `CreateButtons(ctx, m)`.

**성격**: 순수 extract-method + 변수→struct 필드 리네임. 모든 좌표식이 글자 그대로 이동했고 공개 동작·config 키·픽셀 출력 무변. 새 매직넘버 0 — `BuildLayout` 의 `Math.Max(1,…)`·`pad*2`·`*2`·`/2` 는 전부 원본 `BuildChildren` 에서 그대로 옮긴 것. 클래스 doc-comment 의 partial 3분할 설명을 4분할로 갱신.

## 왜 (결정 1) — 레이아웃 메트릭을 명시적 struct 로 (A안 27필드, `BuildLayout` 팩토리)

IMP-2 가 오래 보류였던 이유는 단 하나 — `y` 좌표 누적이 행 루프를 관통해 스크롤바·버튼으로 흐르는 **단일 결합점** 때문에 순진한 extract-method 가 픽셀 회귀를 부르기 쉽고, 다이얼로그 레이아웃은 verifier 자동검증이 불가(수동 smoke 만)해 회귀를 자동으로 못 잡는다는 점이었다 (AUDIT IMP-2 셀의 보류 근거).

해법은 보류 시 권장한 그대로 — **레이아웃 메트릭을 구조체화**. 21개 흩어진 지역변수(스케일된 메트릭 + 파생 좌표)를 `SettingsLayout` record struct 한 곳에 모으면:

- 헬퍼들이 `in SettingsLayout m` 1개만 받아 시그니처가 안정 — 21개 인자를 줄줄이 넘기는 대안(B안) 회피.
- 파생식(`controlColW = Math.Max(1, Math.Min(controlColW, inner-labelCol-colGap))` 등)이 `BuildLayout` 1곳에 모여 **단위 테스트가 박제할 표면**이 생긴다 (결정 4).
- `BuildLayout` 이 HWND 무의존 순수 산술이라 `DialogShellContext` 만으로 호출 가능 → 헤드리스 테스트.

record struct 채택 — 27필드 positional 생성자로 한 번에 채우고 값 의미(불변)라 의도에 맞음. App 레이어 배치라 P6(App→Core 단방향) 위반 0 — Core 로 끌어올리지 않았다(`SettingsLayout` 은 SettingsDialog 전용, 다른 다이얼로그와 공유 안 함).

## 왜 (결정 2) — static 부작용 가시성: `BuildChildren` 집중 + 리스트 2개는 `LayoutRows` 직접

`SettingsDialog` 는 `partial class` 라 cross-file static 필드를 공유한다. 분해 시 부작용을 어디서 쓸지가 가장 미묘했다. 정책:

- **스칼라 static 3개**(`_lineHeight`/`_viewportClientH`/`_scrollMax` — 선언은 [`SettingsDialog.Scroll.cs`](../../App/UI/Dialogs/SettingsDialog.Scroll.cs))의 **대입은 `BuildChildren` 에 집중**. 헬퍼는 값을 반환하고 대입은 오케스트레이터가 한다. 예: `_viewportClientH = m.VpH;`, `_scrollMax = Math.Max(0, totalContentH - _viewportClientH);` 가 BuildChildren 본문에 보인다 → 스크롤 메트릭이 어디서 결정되는지 한눈에.
- **리스트 적재 부작용 2개**(`_scrollChildren` 스크롤 추적 / `_fieldInputs` 커밋 순회)는 **`LayoutRows` 가 직접** 한다. 행을 만들며 누적해야 해서 반환값으로 빼낼 수 없다(자식 N개 × `Add`). 이건 `LayoutRows` 의 XML doc 에 "부작용을 가진다" 로 명시.

핵심은 **관측 가능성** — 스크롤 메트릭(전역 동작에 영향)은 1지점에 모으고, 행 적재(LayoutRows 국소 책임)는 그 자리에 둔다. `_hwndViewport`/`_hwndDialog` 도 BuildChildren 이 대입(`CreateViewport` 는 HWND 반환만).

## 왜 (결정 3) — `LayoutRows` 내부(행 루프)는 분할 안 함

행 루프 자체를 섹션 헤더 생성 / 행 생성 / switch 3-way 로 더 쪼갤 수 있었으나 **하지 않았다**. 이유: 루프 본문이 누적 `y` 를 매 행 갱신(`y += sectionHeadH + sectionHeadGap` / `y += rowH + rowGap` 등)하는데, 이를 별도 메서드로 빼면 `ref int y` 전파가 생기고 — 그게 바로 IMP-2 를 위험하게 만들던 단일 결합점을 **메서드 경계 너머로 흩뿌리는** 짓이다. 회귀 위험이 오히려 커진다.

대신 `LayoutRows` 를 **단일 메서드로 유지**하되 누적 y 를 콘텐츠 총높이(`return y + m.ContentPadInner;`)로 **반환**해, 결합점을 시그니처에 박제했다. 호출자는 그 반환값으로만 스크롤 메트릭을 계산 — `y` 는 LayoutRows 밖으로 새지 않는다. 분해의 동등성을 지키는 핵심 선택.

## 왜 (결정 4) — `SettingsLayoutTests` 로 파생식 자동 박제 (육안 부담↓)

[`tests/KoEnVue.Tests/Unit/SettingsLayoutTests.cs`](../../tests/KoEnVue.Tests/Unit/SettingsLayoutTests.cs) 8 Fact. `BuildLayout` 이 순수 산술이라 `DialogShellContext` 를 HWND 없이 구성(`MakeCtx(dpiScale, pad, dlgW, dlgH)`)해 헤드리스로 돌린다 — `MergeWithDefaults`/`BuildAnimationConfig` 류 순수 표면 단위 테스트 철학 사례 (`BuildLayout` 가시성 `internal`, `InternalsVisibleTo`).

박제 대상 = 분해 시 가장 깨지기 쉬운 clamp/Max 파생식:

| Fact | 박제식 |
|------|--------|
| `LabelX_EqualsContentPadInner` | `LabelX = ContentPadInner`(@96 = 12) |
| `ControlX_IsContentPadInnerPlusLabelColPlusGap` | `ControlX = 12 + 220 + 14` |
| `ControlColW_UnclampedWhenViewportIsWide` | `Min(260, 286) = 260` (clamp 미발동) |
| `ControlColW_ClampedToInnerWidthWhenNarrow` | DlgW=400 → `Min(260, 86) = 86` |
| `ControlColW_FloorIsOneWhenInnerTooSmall` | DlgW=250 → `Max(1, -64) = 1` |
| `SectionContentW_IsAtLeastInnerWidth` | `Max(520, 494) = 520` |
| `Viewport_GeometrySubtractsHeaderButtonAreaAndPads` | VpX/Y/W/H = Pad / Pad+DescH+DescGap / ClientW-Pad*2 / ClientH-VpY-BtnAreaH-Pad |
| `At150Dpi_ScalesLogicalMetricsButNotInjectedPad` | RowH=36·LabelColW=330(×1.5), Pad=24(주입값 그대로) |

마지막 Fact 의 함정 — `Pad` 는 호출자(DialogShell)가 이미 DPI 스케일해 `Metrics.Pad` 로 주입하므로 `BuildLayout` 이 재스케일하면 안 된다(2중 스케일 버그). 테스트가 150% 에서 logical 메트릭만 ×1.5 되고 주입 Pad 는 불변임을 박제한다.

## 왜 (결정 5) — 픽셀 회귀는 자동검증 불가 → 수동 육안검증 체크리스트

`SettingsLayoutTests` 가 파생식은 잡지만 **행 y 누적·실제 GDI 렌더는 자동검증 불가**(GDI/`CreateWindowExW`/실 DPI 의존). 분해 후 한 번은 사람이 눈으로 확인한 14섹션 레이아웃 체크리스트:

1. **14섹션 모두 표시** — 일반 / 일반-트레이 / 인디케이터-색상(공용) / 플로팅 배지 8 / 커서 헤일로 2 / 고급. 섹션 헤더 라벨 + ETCHED 구분선 정상.
2. **행 정렬** — 라벨(좌)·입력 컨트롤(우) 열 정렬. `LabelX`/`ControlX` 일관.
3. **행 간격** — 섹션 헤더 후 / 행 사이 / 섹션 사이 간격이 종전과 동일(헤더 `sectionHeadH+sectionHeadGap`, 행 `rowH+rowGap`, 구분선 후 `sectionSepPostGap`, 섹션 상단 `sectionTopGap`).
4. **컨트롤 종류별 크기** — Bool 체크박스(`rowH×rowH`) / Combo(`controlColW × rowH+comboDropExtra` 드롭다운) / EDIT(`controlColW × rowH`).
5. **컨트롤 폭 clamp** — 다이얼로그 폭을 좁혀도 컨트롤이 뷰포트 밖으로 안 삐져나옴(`ControlColW` clamp 발동).
6. **스크롤** — 콘텐츠가 뷰포트보다 길어 세로 스크롤바 출현. 휠/드래그/PageUp·Down 정상. `_scrollMax` 가 정확(맨 아래까지 스크롤되되 그 이상 안 됨).
7. **ScrollIntoView** — Tab 으로 포커스 이동 시 가려진 컨트롤이 화면 안으로 스크롤.
8. **버튼** — OK/Cancel 하단 중앙 정렬(`btnX = (ClientW - (BtnW*2+Pad))/2`), 크기 `BtnW×BtnH`.
9. **DPI 100/125/150%** — 각 배율에서 1~8 재확인(특히 컨트롤 폭·폰트·간격이 배율 따라 스케일, Pad 2중 스케일 없음).
10. **커밋** — 값 수정 후 OK → 저장 반영, Cancel → 무시. `_fieldInputs` 순서가 `BuildRowDefs` 와 일치(섹션 재배치 후에도).

게이트 결과: AOT publish exe **4,883,456 B** (SHA256 `6CE0F2D804C4060475378EE352D701F6951999397C9016C0D84EB222CB54F613`), 테스트 **90/90** PASS(신규 8), reviewer P1–P6 0위반 + 분해 동등성 invariant 6개 보존.

## 관련

- 코드: [`SettingsDialog.cs`](../../App/UI/Dialogs/SettingsDialog.cs) · [`SettingsDialog.Layout.cs`](../../App/UI/Dialogs/SettingsDialog.Layout.cs) · [`SettingsLayoutTests.cs`](../../tests/KoEnVue.Tests/Unit/SettingsLayoutTests.cs)
- 문서: [architecture.md](../architecture.md) SettingsDialog 행 · [implementation-notes.md § SettingsDialog](../implementation-notes.md) · [conventions.md 테스트 카탈로그](../conventions.md)
- 추적: [AUDIT-2026-06-02-codebase-review.md](../improvement-plan/AUDIT-2026-06-02-codebase-review.md) IMP-2
