# 커서 추종 인디케이터 — 엔진 분리 + P4 예외 정당화 (2026-05-27)

> **상태**: PR-B 완료 (3 commits — PR-B-1 엔진/Style/Renderer + PR-B-2 AppConfig 10 키 + PR-B-3 App 파사드 `CursorOverlay` + 트레이 토글 + 사용자 가시 통합). 사용자가 트레이 메뉴 "커서 인디케이터" 체크박스로 즉시 활성화 가능. 본 섹션은 PR-B 종합 회고.

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

## cursor-tray 브랜치 학습 결과

cursor-tray 브랜치 (19 commits, 실험적 prototyping) 의 결과를 본 PR-B 에서 무엇 채택 / 무엇 거부했는지:

- **채택**:
  - **WS_EX_TRANSPARENT 영구 ON** — cursor 인디 윈도우는 사용자 드래그/hit-test 가 필요 없으므로 클릭 통과를 OS 차원에서 보장하는 가장 단순한 방식. [dev-notes/2026-05-15-click-through-attempts.md](2026-05-15-click-through-attempts.md) F2 패턴을 메인 인디에는 못 썼지만 cursor 인디에는 자연 적용
  - **별도 HWND** — 메인 `_hwndOverlay` 와 분리. 같은 윈도우 클래스로 두 인스턴스 생성 가능 (WNDCLASSEXW 1회 등록 + CreateWindowExW 2회 호출)
  - **트레이 메뉴 체크박스 토글** — 라벨 자체가 기능명 (`I18n.MenuCursorIndicator = "커서 인디케이터" / "Cursor indicator"`), MF_CHECKED = ON. "고급 → 보조 인디" 같은 서브메뉴 계층 없이 우클릭 메뉴 한 클릭 노출 — 발견 가능성 우위
- **거부**:
  - **`WH_MOUSE_LL` 글로벌 마우스 후킹** — NativeAOT 콜백 risk + 300ms OS timeout 위반 시 silent 비활성화 + 다중 모니터 좌표 정규화 부담. `WM_TIMER` 50ms 폴링이 cursor 정지 검출에 충분하고 항상 표시 모드도 16ms 폴링으로 사용자 인지 가능 부드러움 달성
  - **`WM_INPUT` (raw input)** — 마우스 device handle 등록 + 메시지 분기 + 좌표 변환 비용이 정지 검출 정도의 요구에 과대. 폴링 모델로 충분
  - **설정 다이얼로그 신규 섹션** — cursor 인디는 디폴트 OFF 라 다이얼로그 노출 시 일반 사용자 대상 노이즈. config.json 직접 편집으로 가이드 (config-reference.md). 활성화 사용자 수 충분히 누적되면 다이얼로그 추가 재검토
- **부분 채택**:
  - **항상 표시 모드** — cursor-tray 의 "항상 표시 + fade" 디자인에서 fade 부분 제거. 정지 검출 모드와 항상 표시 모드 양분, fade 는 alpha race 와 상호작용 위험 회피
  - **CAPS 정책** — cursor-tray 의 "CAPS 별도 색상" 에서 "한글/비한글 같은 카테고리, 영문만 반대편" 으로 단순화 (사용자 인터뷰). 색상 3개 (Hangul/English/NonKorean) 중 2개 만 cursor 인디에 노출, 다이얼로그 단순화

## PR-B-3 트레이 메뉴 정책 인터뷰 결정

PR-B-3 진입 직전 사용자와 트레이 메뉴 UI 결정:

- **위치**: `IDM_USER_HIDDEN` ("인디케이터 숨김") 바로 아래. 기존 사용자 가시 인디 토글 라인과 함께 묶어 "인디 ON/OFF" 의미 그룹화
- **라벨**: "커서 인디케이터" — 기능명을 라벨로 직접. 별도 ▸ 화살표 + 서브메뉴 계층 없이 한 클릭 토글. 기능 1개 = 메뉴 항목 1개
- **체크 의미**: MF_CHECKED = ON (활성) — `user_hidden` 의 "체크 = 숨김" 반대 의미와 혼동 가능하나, cursor 인디는 "기능 자체 ON/OFF" 라 일반 토글 의미 (체크 = 켜짐) 가 자연. 사용자도 이 의미 통일에 동의
- **부수 메뉴 미추가**: 반지름/두께/색상은 config.json 편집 가이드. 디폴트 OFF + 디폴트 사용자가 켜는 첫 순간에 동심원이 즉시 보이므로 GUI 튜닝 미노출 결정

## 관련

- 메인 인디 알파 race fix (선행 PR-A): [dev-notes/2026-05-27-snap-fade-killtimer.md](2026-05-27-snap-fade-killtimer.md)
- 메인 인디 엔진: [Core/Windowing/LayeredOverlayBase.cs](../../Core/Windowing/LayeredOverlayBase.cs)
- 본 PR-B-1 의 파일: `Core/Windowing/{CursorStyle,LayeredCursorBase}.cs` + `App/UI/CursorRenderer.cs`
