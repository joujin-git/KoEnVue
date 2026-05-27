# 커서 추종 인디케이터 — 엔진 분리 + P4 예외 정당화 (2026-05-27)

> **상태**: PR-B 진행 중. 본 문서는 PR-B-1 (Core 엔진 + Style + Renderer 3 모듈 도착) 시점에 작성. PR-B-2 (감지 / 타이밍 / 위치 추종) + PR-B-3 (App 파사드 `CursorOverlay` + 트레이 / 설정 다이얼로그 통합 + CHANGELOG narrative) 진행하며 본 문서 update.

## 무엇 (What — PR-B-1 시점)

신규 3 파일로 커서 추종 인디케이터의 렌더 엔진 + Style + Renderer 도착. 사용자 가시 기능 미완성 — PR-B-3 도착 시 트레이 / 설정 다이얼로그 토글로 활성화 가능.

| 파일 | LOC | 역할 |
|------|-----|-----|
| `Core/Windowing/CursorStyle.cs` | ~55 | `CursorStyle` (engine 입력 10 필드) + `CursorMetrics` (engine→callback 출력 3 필드) record struct 쌍 + `BoundingBoxLogicalPx` 헬퍼 |
| `Core/Windowing/LayeredCursorBase.cs` | ~250 | `LayeredOverlayBase` 의 형제 엔진 — DIB 생성 + premultiply + `UpdateLayeredWindow` 만 책임. 콜백 시그니처 `Func<IntPtr ppvBits, CursorStyle, CursorMetrics, (int w, int h)>` |
| `App/UI/CursorRenderer.cs` | ~150 | distance-field 분석적 AA 픽셀 셰이더. 동심원 3개 (Inner / Middle / Outer) + 코어 / 헤일로 분리. CAPS OFF 시 Outer skip |

## 왜 별도 엔진 (P4 예외 정당화)

### 기본 원칙 vs 본 결정

CLAUDE.md P4: "하나의 구현만 — 공유는 `Core/`". `LayeredOverlayBase` (메인 인디 엔진) 가 이미 DIB 생성 / premultiply / `UpdateLayeredWindow` 의 ~120 LOC 를 보유. 본 PR-B 의 cursor 인디도 동일 패턴이 필요 — 통상이라면 `LayeredOverlayBase` 를 generic 화 (예: callback 시그니처를 다양화하거나 책임을 더 잘게 쪼개거나) 해서 공유했어야 함.

### 본 결정: 별도 엔진 (`LayeredCursorBase`) 신규 작성

**1차 근거**: 메인 인디는 알파 race 미해결 영역 (부팅 시점 깜박임 #2/#3 — 본 세션 PR-A 의 `SnapToTargetAlpha` Fade KillTimer fix 가 부분 해소했으나 detection thread 의 3 메시지 연쇄 자체는 여전) 이 있어, 메인 엔진에 새 변경면 (예: 콜백 시그니처 일반화, 새 책임 주입) 을 추가하면 회귀 위험. 본 PR-B 의 신규성 (커서 추종 / 동심원 / 헤일로) 을 메인 엔진 분해/일반화와 합치는 것은 위험·범위 양쪽 증가.

**2차 근거**: 콜백 시그니처 자체가 본질적으로 다르다 — 메인 엔진은 GDI 그리기 (DrawTextW / RoundRect) 를 사용하므로 콜백에 `hdc` 를 전달하고 내부에서 `GetCurrentObject` + `GetObjectDibSection` 으로 DIB ppvBits 재추출. cursor 엔진은 GDI 그리기 사용 없이 픽셀 셰이딩만 수행하므로 `ppvBits` 를 직접 전달. 일반화하려면 메인의 `Gdi32.cs` 의 `GetCurrentObject` / `GetObjectDibSection` 시그니처 유지 + 새 분기 추가가 되는데, 이는 메인 인디 핫패스에 변경면 추가.

**3차 근거**: 책임 표면 차이가 크다 — 메인 엔진은 폰트 (`EnsureFont` + `GetTextMetricsW` + `_textVCenterOffsetPx` 캐시) / 드래그 (`BeginDrag` + `HandleMoving` + `EndDrag`) / 라벨 측정 (`CalculateFixedLabelWidth` + 7-key 캐시) / `WindowSnapHelper` 위임의 4 책임 영역을 보유. cursor 엔진은 이 중 0 개가 필요 — 폰트도, 드래그도, 라벨도, 스냅도 없음. 공통 분모는 진정으로 ~120 LOC 의 DIB 생성 + premultiply + UpdateLayeredWindow 뿐.

### 비용 vs 편익

**비용**: ~120 LOC 중복 + 향후 어느 한쪽 버그 fix 시 양쪽 동시 점검 부담 (예: `CreateDIBSection` 실패 처리 / `[LibraryImport]` Win32 호출 형식 변경 / DPI 갱신 정책 변경).

**편익**: 메인 인디 핫패스의 변경면 0 + cursor 엔진의 단순화 (250 줄 << 메인 엔진 767 줄, 책임 1/4 수준). 회귀 차단 가치가 비용 우위.

### Update 트리거 (재검토 조건)

다음 조건이 발생하면 본 결정 재검토 — `LayeredOverlayBase` ↔ `LayeredCursorBase` 통합 후보:

1. 메인 인디 알파 race 영역 (`OverlayAnimator._phase` / `_currentAlpha` 와 `LayeredOverlayBase._lastStyle` 의 상호작용) 이 완전 해소되어 메인 엔진 표면에 변경 위험이 사라짐
2. 양 엔진 모두에 동일한 회귀 (예: Win32 API 변경, DPI 정책 변경, DIB 생성 실패 처리 변경) 가 반복 3회 이상 발생해 중복 유지 비용이 회귀 차단 가치를 초과
3. cursor 엔진에 폰트 / 드래그 / 라벨 등 책임이 추가되어 책임 표면 차이 우위가 사라짐

## cursor-tray 브랜치 학습 결과 (To Be Filled)

> 사용자가 PR-B-2/3 진행하며 채울 영역. PR-B-1 시점에는 placeholder.

cursor-tray 브랜치 (19 commits, 실험적 prototyping) 의 결과를 본 PR-B 에서 무엇 채택 / 무엇 거부했는지:

- 채택:
  - (TBD — PR-B-2/3 진행하며 추가)
- 거부:
  - (TBD)
- 부분 채택 / 수정 채택:
  - (TBD)

## 다음 단계 (PR-B-2/3 진입 조건)

- **PR-B-2** (감지 / 타이밍 / 위치 추종):
  - 커서 위치 추적 메커니즘 (`WM_TIMER` poll vs `WH_MOUSE_LL` hook vs `WM_INPUT` raw input)
  - IME 상태 변경 시 인디 표시 / fade-out 타이밍
  - 다중 모니터 / DPI 변경 처리
- **PR-B-3** (App 파사드 + 통합):
  - `App/UI/CursorOverlay.cs` 파사드 — `LayeredCursorBase` 인스턴스 보유 + `BuildStyle(config, state)` 가 `ImeState` → `CursorStyle` 합성
  - 트레이 메뉴 토글 + 설정 다이얼로그 섹션 (반지름 / 두께 / 색상 / `HaloOpacity` 등)
  - `docs/config-reference.md` 신규 키 등재
  - `docs/User_Guide.md` 사용자 가이드 항목 추가
  - CHANGELOG `## [Unreleased]` `### Added` narrative 한 줄 (사용자 가시 기능 완성 시점)

## 관련

- 메인 인디 알파 race fix (선행 PR-A): [dev-notes/2026-05-27-snap-fade-killtimer.md](2026-05-27-snap-fade-killtimer.md)
- 메인 인디 엔진: [Core/Windowing/LayeredOverlayBase.cs](../../Core/Windowing/LayeredOverlayBase.cs)
- 본 PR-B-1 의 파일: `Core/Windowing/{CursorStyle,LayeredCursorBase}.cs` + `App/UI/CursorRenderer.cs`
