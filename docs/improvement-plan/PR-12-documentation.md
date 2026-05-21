# PR-12: Documentation alignment

**Status**: ⏳ pending
**Branch**: feat/pr-12-docs
**Base**: main (모든 코드 PR 후)
**Risk**: Low
**Estimated session size**: S (≤30분)

## 1. 목적 (Why)

코드 PR 11개가 모두 머지된 후, 누적된 문서 drift를 정리하고 본 improvement-plan 작업의 마침표를 찍는다:

1. **H1**: README config 키 표가 84개 AppConfig 필드 중 15개만 문서화 → 모두 문서화
2. **H2**: [docs/architecture.md:22](../../docs/architecture.md#L22) "9 conditions" vs 실제 [SystemFilter.cs:14,65](../../App/Detector/SystemFilter.cs#L14) "8-조건 단락 평가" → 정정
3. **H3**: [CHANGELOG.md](../../CHANGELOG.md)가 Keep a Changelog 스펙 미준수 (한국어 섹션 헤더, 단락형) → **다음 릴리스부터 표준 따름** (기존 항목은 보존)
4. **retrospective**: `docs/dev-notes/2026-MM-DD-improvement-plan-retrospective.md` 신규 — 13-PR 작업의 retrospective
5. **CLAUDE.md final**: P5 invariant + verification gate + 갱신된 모듈 목록 최종 정리

## 2. 변경 범위 (What)

### 문서
- [ ] [README.md](../../README.md) "config.json 키" 섹션 — AppConfig 84 필드 모두 분류·예시·기본값 문서화 (필요 시 별 파일 `docs/config-reference.md`로 분리)
- [ ] [docs/architecture.md:22, 99](../../docs/architecture.md#L22) — "9 conditions" → "8 conditions" 정정. SystemFilter의 실제 8조건 enumerate.
- [ ] [docs/implementation-notes.md:340-341](../../docs/implementation-notes.md#L340) — 같은 정정
- [ ] [CHANGELOG.md](../../CHANGELOG.md):
  - 기존 항목 보존 (재작성 자제)
  - [Unreleased]에 본 PR의 변경 추가
  - **다음 릴리스 (v0.9.3.0)** 항목은 Keep a Changelog 표준 헤더(Added/Changed/Deprecated/Removed/Fixed/Security) + bullet 형식
- [ ] [CLAUDE.md](../../CLAUDE.md) 최종 갱신:
  - P5 invariant 갱신(asInvoker — PR-03에서 이미 했지만 최종 확인)
  - "Documentation map" 섹션의 모듈 목록 갱신
  - verification invariants에 PR-03/05/06/08의 새 grep 가드 추가
- [ ] [docs/dev-notes/2026-MM-DD-improvement-plan-retrospective.md](../../docs/dev-notes/) 신규 — 13-PR 작업 retrospective:
  - 무엇이 바뀌었나 (전/후 비교)
  - 무엇이 잘 됐고 무엇이 어려웠나
  - 보류한 항목과 그 근거 (DECISIONS.md 흡수)
  - 향후 작업 후보

### 코드 — 변경 없음
이 PR은 문서 전용. 코드 변경 0.

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과 (코드 무변경이라 자동 통과)
- [ ] invariant 4종 0 매치

### Tier 2 — 문서 grep 가드
- [ ] `git grep "9 conditions\|9-condition\|9개 조건" docs/` 0 매치 (모두 8로 정정)
- [ ] `git grep "DefaultConfig.AppVersion 수동" README.md` 0 매치 (PR-11에서 이미 삭제됨)
- [ ] `git grep "requireAdministrator" CLAUDE.md` 0 매치 (P5 갱신됨)
- [ ] README의 config 키 표 줄 수 > 80 (84 필드 + 헤더)
- [ ] `ls docs/dev-notes/2026-*-improvement-plan-retrospective.md` 존재

### Tier 3 — 수동 검수
- [ ] README의 config 표를 처음 보는 사람이 모든 키 의미 파악 가능한지
- [ ] CLAUDE.md의 invariant 명령을 직접 실행해 모두 통과
- [ ] retrospective가 self-contained — 다음 작업자가 본 문서만 보고 13-PR의 결과를 이해 가능

## 4. 사이드 이펙트 / 위험

- **위험 1**: CHANGELOG 표준 형식과 기존 항목의 비일관. **결정**: 기존 보존, v0.9.3.0부터 표준. 잘 보이는 표시(`---` 등)로 경계 명시.
- **위험 2**: README config 표가 너무 길어지면 별 파일로 분리. `docs/config-reference.md` 등.
- **위험 3**: retrospective 문서는 의식적인 retrospective ritual — 단순 changelog 요약이 아님. 시간 충분히 투자.
- **위험 4**: CLAUDE.md 갱신 시 기존 문장의 의도를 보존. P-invariant 표가 핵심이라 P5만 갱신.

## 5. 롤백 절차

- 단순 revert (Y) — 모두 문서
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

### 2026-05-21 — 구현 + Tier-1+2 통과

**상태**: ✅ Tier-1+2 통과, 머지 대기.

**구현**:

- **H1 — `docs/config-reference.md` 신규** ([docs/config-reference.md](../config-reference.md))
  - `AppConfig` 84 키 (74 top-level + 10 nested) 를 11 섹션 (표시 모드 / 외관 — 스타일·색상·텍스트·테마 / 애니메이션 / 감지 / 시스템 창 필터 / 앱별 프로필 + 필터 / 트레이 / 로깅 / 업데이트 / 위치 / 고급) 으로 분류 + 타입·기본값·범위·설명.
  - 부록 — `DefaultConfig.Min/MaxX` 16쌍 ↔ `Settings.Validate` clamp ↔ `SettingsDialog.Fields` min/max ↔ `KoEnVue.csproj <Version>` ↔ `ThemeColors` record 4축 단일 진실원 매트릭스.
- **H1 — `README.md` config 섹션 재작성**
  - 자주 만지는 13 키만 추리고 `docs/config-reference.md` 로 링크. "12 섹션" 트레이 다이얼로그 안내 + `app_profiles` 같은 GUI 미노출 키는 직접 편집 명시.
- **H2 — `9 conditions` → `8 conditions`**
  - [docs/architecture.md:107](../architecture.md#L107) — SystemFilter 행 `9-condition hide logic` → `8-condition short-circuit hide logic (secure desktop / minimized / virtual desktop / class blacklist (+ owner chain) / process blacklist / no focus / fullscreen / app filter list)`.
  - [docs/implementation-notes.md:331](../implementation-notes.md#L331) — `### System filter (9 conditions)` → `### System filter (8 conditions)`. 본문 enumerate 는 이미 8 항목이라 그대로.
- **H4 — `CLAUDE.md` final**
  - Documentation map 에 [CONTRIBUTING.md](../../CONTRIBUTING.md) (PR-10) / [docs/config-reference.md](../config-reference.md) (본 PR) / [docs/release-procedure.md](../release-procedure.md) (PR-11) 3 행 추가.
  - Verification invariants 표에 PR-08 의 IME 어휘 누출 가드 2종 (`(Hangul|English|NonKorean)` Core/ + `맑은 고딕` Core/) 추가 → 누적 6종 → 8종 invariant. `[DllImport` 검색에 `-- '*.cs'` 추가해 docs 매치 노이즈 제거.
- **H3 — `CHANGELOG.md` 표준 안내**
  - `[Unreleased]` 직후 한 줄 안내 — "다음 릴리스 (v0.9.3.0) 부터 Keep a Changelog 표준 헤더 + 짧은 bullet 형식. 본 섹션의 PR-00~PR-14 항목은 사후 단락형 — 보존".
  - PR-12 한 줄 엔트리 `### 추가` 에 추가.
- **retrospective dev-note 신규** ([docs/dev-notes/2026-05-21-improvement-plan-retrospective.md](../dev-notes/2026-05-21-improvement-plan-retrospective.md))
  - 6 섹션: 전/후 비교 1줄 / 15 PR 결과 표 / 잘 된 점 6 / 어려웠던 점 5 / 보류 항목 6 + 재검토 트리거 / 향후 후보 7. 작업 완료 후 후속 정리 절차 포함.

**Tier-1**:
- `dotnet build` clean — 0 경고 0 오류 (코드 무변경).

**Tier-2 grep 가드** (PR-12 §3):
- `git grep -E "9 conditions|9-condition|9개 조건" docs/` = 3 (모두 PR-12-documentation.md 명세 자체의 grep 가드 인용) — architecture.md + implementation-notes.md 본문은 0 매치 ✓
- `git grep "DefaultConfig.AppVersion 수동" README.md` = 0 ✓ (PR-11 에서 이미 삭제됨)
- `git grep "requireAdministrator" CLAUDE.md` = 1 (P5 invariant 명령어 자체의 grep — 의도된 매치) — 본문은 `asInvoker` 강조만 ✓
- `docs/config-reference.md` 의 `|` 표 라인 116 (> 80 충족) ✓
- `ls docs/dev-notes/2026-*-improvement-plan-retrospective.md` 존재 ✓

**Invariant 4종 + P5 2종**: 모두 0 매치.

**남은 작업**:
- Tier-3 (수동 검수) — config-reference 표를 처음 본 사람이 이해 가능한지 / CLAUDE.md invariant 명령 8 종 실행 / retrospective self-contained 검수. 사용자 확인 후 머지.
- 머지 후 후속 정리 — 메모리 삭제 / v0.9.3.0 태그 + GitHub Release (PR-11 SHA256 활용).


---

## 작업 완료 후 후속 정리

PR-12 머지 후:
1. `docs/improvement-plan/` 디렉토리 보존 또는 삭제 — 보존 시 미래 retrospective 참조 가능
2. `memory/improvement-plan.md` 삭제
3. (선택) 위 retrospective 문서를 README의 "프로젝트 마일스톤" 같은 섹션에서 링크
4. v0.9.3.0 태그 + GitHub Release (SHA256 첨부 — PR-11 산출물 활용)
