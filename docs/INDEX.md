# docs/ Index

KoEnVue 문서 전체 인덱스. [CLAUDE.md](../CLAUDE.md) 는 가장 기본적인 P1–P6 규칙만 포함하고, 자세한 내용은 모두 이곳에서 시작.

## 사용자 가이드

| 파일 | 용도 |
|------|------|
| [User_Guide.md](User_Guide.md) | 최종 사용자 매뉴얼 (한국어) |
| [KoEnVue_PRD.md](KoEnVue_PRD.md) | 제품 요구사항 — 기능 스펙, 동작, config |
| [config-reference.md](config-reference.md) | `config.json` 전체 키 레퍼런스 |

## 개발 / 기여

| 파일 | 용도 |
|------|------|
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | 환경/빌드/테스트/AOT publish |
| [architecture.md](architecture.md) | Core/App 모듈 분리, 재사용 계약, facade 패턴 |
| [implementation-notes.md](implementation-notes.md) | 렌더 파이프라인, 드래그/스냅, 애니메이션, CAPS LOCK, hot reload, dialogs, update check |
| [conventions.md](conventions.md) | P1–P6 enforcement, silent catch policy, .NET 10 quirks, invariant grep |
| [release-procedure.md](release-procedure.md) | 릴리스 절차 — csproj `<Version>` bump + publish + GitHub Release + SHA256 |

## Claude Code 하네스

| 파일 | 용도 |
|------|------|
| [harness.md](harness.md) | 하네스 전체 레퍼런스 — subagents, hooks, slash commands, sessions |
| [sessions/](sessions/) | 세션별 작업 기록 (장비 간 연속성) |

## 회고 / 결정 기록

| 파일 | 용도 |
|------|------|
| [improvement-plan/INDEX.md](improvement-plan/INDEX.md) | PR-XX 형식 개선 계획 인덱스 |
| [improvement-plan/DECISIONS.md](improvement-plan/DECISIONS.md) | 누적된 설계 결정 |
| [dev-notes/](dev-notes/) | 실패한 구현 시도의 postmortem (같은 함정 재방문 방지) |

## 변경 이력

| 파일 | 용도 |
|------|------|
| [../CHANGELOG.md](../CHANGELOG.md) | 릴리스 이력 (Keep a Changelog 형식) |
| [../README.md](../README.md) | 다운로드 / 빌드 / 릴리스 진입점, CI 배지 |
