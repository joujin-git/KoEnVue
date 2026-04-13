# 08 — Stage 7: 전체 최종 검증

← Previous: [07 — Stage 6](07-stage6-docs-sync.md) | → Next: [09 — Risks & Reuse](09-risks-and-reuse.md)

## 목표

Stage 0~6까지 14개 커밋이 얹힌 최종 상태에서 Core 독립성·문서 정합성·런타임 안정성을 **하나의 게이트**로 닫는다. 이 게이트를 통과하지 못하면 Stage 5/6의 뒷처리가 부족한 것이므로 해당 단계로 돌아간다.

## 에이전트 구성

- **구성**: serial, 1x Explore + 1x general-purpose

## Phase 1 — Explore 에이전트 (read-only 감사)

### 실행
- `Core/**/*.cs` 전수 grep:
  - `KoEnVue.App`
  - `using KoEnVue.App`
  - `ImeState`
  - `AppConfig`
  - `DefaultConfig`
  - `I18n`
  → 모두 0건 혹은 허용 리스트에 포함
- Core 모듈 각각이 독립적으로 이해 가능한지 간단히 체크 (공개 API, 의존하는 Core 동료만)
- 네이밍 일관성: Core 파일은 `Core.*` 네임스페이스, App 파일은 `App.*` 네임스페이스에 있는지
- CLAUDE.md의 Project Structure 트리와 실제 폴더 구조가 글자 단위로 일치

## Phase 2 — general-purpose 에이전트 (최종 게이트 실행)

### Final Verification Gate — 아래 12개 항목 모두 녹색일 때만 완료

| # | 항목 | 방법 |
|---|---|---|
| 1 | 빌드 클린 | `dotnet clean && dotnet build && dotnet publish -r win-x64 -c Release` — 에러 0, Stage 0 대비 경고 증가 0 |
| 2 | publish exe 기동 | `bin/Release/net10.0-windows/win-x64/publish/KoEnVue.exe` 실행 → 인디케이터 표시 |
| 3 | IME 토글 반영 | 한/En 전환 시 라벨 변경 + 슬라이드/하이라이트 애니메이션 동작 |
| 4 | 트레이 메뉴 | 우클릭 메뉴 전 항목 한국어 표시, SnapToWindows/AnimationEnabled/ChangeHighlight 토글 체크 상태 보존 |
| 5 | SettingsDialog 종단 | 스크롤/편집/저장 → `config.json` 반영, 헥스 색상 `XYZ123` 같은 잘못된 입력 → `ColorHelper.TryNormalizeHex` 경유 검증 실패 + 해당 필드 포커스 |
| 6 | Config 핫-리로드 | 외부 편집(`Opacity` 변경) → 5초 내 반영 |
| 7 | 파일 삭제 안전성 | `config.json` 일시 삭제 → 기본값 리셋 발생 안 함, 재생성 시 사용자 값 유지 |
| 8 | CLAUDE.md 정합성 | 모든 경로 참조가 실제 파일로 해결. 구 경로 매치 0 |
| 9 | PRD 정합성 | Core/App split 섹션 존재, 경로 참조 모두 유효 |
| 10 | Core 독립성 | `git grep "KoEnVue\.App" Core/` → 0건, `git grep "Detector\." App/Config/` → 0건, `git grep "ImeState" Core/` → 0건 |
| 11 | 멀티모니터 DPI 전환 | 2개 이상 모니터 환경에서 인디케이터를 DPI가 다른 모니터로 드래그 이동 → 경계 교차 시 폰트/라운드 리사이즈 정상, 크래시 없음 |
| 12 | baseline 델타 비교 | `docs/reorg/baseline.md`의 exe SHA256/사이즈 대비 현재 publish된 exe 크기 델타 ≤ +100KB, 경고 수 변화 0 |

### 추가 부가 제약
- 전체 작업 중 추가된 NuGet 패키지 0건 (P1)
- `app.manifest requireAdministrator` 유지 (P5) — `git diff master -- app.manifest` 결과가 비어 있는지 확인
- `docs/reorg/baseline.md`가 삭제되지 않고 유지됨 (C0 커밋이 revert되지 않음)

## 실패 시 돌아가야 할 단계

| 실패 항목 | 원인 가능성 | 복귀 Stage |
|---|---|---|
| 1, 12 | 경고/크기 회귀 | Stage 5 또는 관련 Stage 4 서브태스크 |
| 2-7, 11 | 런타임 회귀 | Stage 5로 돌아가 재검증 |
| 8, 9 | 문서 경로 누락 | Stage 6 재작업 |
| 10 | Core 의존성 역류 | Stage 4 재검토 (LayeredOverlayBase/OverlayAnimator의 ImeState 누출 여부) |

## 커밋 출력

**없음** — Stage 7은 검증 전용.

---

← Back to [README](README.md)
