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
  - **다음 릴리스 (v0.10.0)** 항목은 Keep a Changelog 표준 헤더(Added/Changed/Deprecated/Removed/Fixed/Security) + bullet 형식
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

- **위험 1**: CHANGELOG 표준 형식과 기존 항목의 비일관. **결정**: 기존 보존, v0.10.0부터 표준. 잘 보이는 표시(`---` 등)로 경계 명시.
- **위험 2**: README config 표가 너무 길어지면 별 파일로 분리. `docs/config-reference.md` 등.
- **위험 3**: retrospective 문서는 의식적인 retrospective ritual — 단순 changelog 요약이 아님. 시간 충분히 투자.
- **위험 4**: CLAUDE.md 갱신 시 기존 문장의 의도를 보존. P-invariant 표가 핵심이라 P5만 갱신.

## 5. 롤백 절차

- 단순 revert (Y) — 모두 문서
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

(empty)

---

## 작업 완료 후 후속 정리

PR-12 머지 후:
1. `docs/improvement-plan/` 디렉토리 보존 또는 삭제 — 보존 시 미래 retrospective 참조 가능
2. `memory/improvement-plan.md` 삭제
3. (선택) 위 retrospective 문서를 README의 "프로젝트 마일스톤" 같은 섹션에서 링크
4. v0.10.0 태그 + GitHub Release (SHA256 첨부 — PR-11 산출물 활용)
