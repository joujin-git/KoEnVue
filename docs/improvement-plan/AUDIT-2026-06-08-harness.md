# AUDIT 2026-06-08 — 하네스 점검 (harness-optimize 워크플로우 산출)

`Workflow({name:'harness-optimize'})` 실행 결과(6 에이전트 병렬 점검 + completeness critic, 42건 제안)의 영구 추적 문서. ultracode 전면 도입(같은 날) 직후 상태 점검.

> 이 문서 자체가 "워크플로우 결과는 반환 즉시 git-tracked 파일로 고정(휘발 방지)" 규약의 첫 적용 사례 (제안 #6).

상태 범례: ✅ 적용 · 🟡 부분적용 · 🔍 검증대기 · 📋 보고/사용자결정 · ⏭️ 다음

## High impact

| # | 영역 | 상태 | 조치 |
|---|------|------|------|
| 1 | 코드→문서 매핑 3곳 중복 (docs-keeper / harness §5 / post-edit hook) | 📋 | 단일 진실원 승격(reviewer §0 패턴) — 구조 변경이라 사용자 결정 후 |
| 2 | 워크플로우 런타임 계약 미문서화 | ✅ | `.claude/workflows/README.md` 신설 — 심볼 계약 + 작성 함정 박제 |
| 3 | **메모리 split-brain** (autoMemoryDirectory 무효 → C: 사용, E:는 백업) | ✅ | 구제(os-dependent E: 복사) + **근본해결: `Sync-Memory` hook**(_common.ps1, SessionStart) — E:=truth, C: 더 새 파일만 흡수, E:→C: 미러. 검증: slug/mtime보존 OK, restored=5, C:=E: 해시 일치 |
| 4 | ultracode fan-out 하 git 쓰기 경합 (index.lock) | 🔍 | leaf 가 실제 git commit 하는지 실측 먼저(현재 leaf=read-only라 위험 낮음 추정). 확정 시 _common 에 Global mutex |
| 5 | 비용 거버너 no-op (budget.total 미주입 시 상한 우회) | 🟡 | bug-hunt `round<8` + release-review `MAX_VERIFY=25` **적용**. codebase-audit/design-compare 는 고정단계라 영향 작음. budget 주입 규약은 ⏭️ |
| 6 | effort 손실 — 워크플로우 결과 휘발 | ✅ | 이 AUDIT 문서가 첫 실천. harness.md 에 "결과 반환 즉시 고정" 규약 ⏭️ 추가 |
| 7 | bypassPermissions + ultracode 무인 fan-out | 📋 | leaf read-only 강제 원칙(README 에 일부 박제). 파괴작업 오케스트레이터 전용 계약 — 정책이라 보고 |
| 8 | verifier `dotnet test` false-pass (CLAUDE.md 규칙 위반) | ✅ | csproj 명시로 교체 + bare 금지 명문화 |
| 9 | ultracode 발동/런타임 검증 | ✅ | Workflow fan-out 확인됨(release-review/harness-optimize 각 6 에이전트). statusLine↔docs 검증 모순 정정(statusLine 은 신호 아님 명시) |

## Medium impact

| 영역 | 상태 | 조치 |
|------|------|------|
| _common Invoke-HookSafely 에러 출처 누락(빈 hook명) | ✅ | scriptblock Ast 로 hook 파일명 기록 |
| session-start 로스터 historian 누락 | ✅ | 6명으로 일치 |
| release-review meta↔phase 정합(Verify 미선언) | ✅ | stage2 에 `phase('Verify')` |
| statusline↔docs 검증 모순 | ✅ | docs/memory 를 "Workflow fan-out 으로 검증" 으로 정정 |
| memory 파일명/wikilink 불일치 | ✅ | wikilink 를 실제 name 으로([[feedback-version-format]] 등) |
| memory git추적 정책 drift(commands/ 표기) | ✅ | skills/workflows/memory 로 갱신 |
| PreCompact hook 부재 | ✅ | `pre-compact.ps1` 신설 (matcher `*` = auto+manual) — (1) 세션 로그에 compaction 마커 append, (2) additionalContext 로 git 스냅샷+세션파일 포인터 주입(연속성 복원). hook 이벤트 7→8개. harness.md §4 반영 |
| inject-turn-context 매 턴 토큰(~700자) | 📋 | N턴마다 전체/이후 축약 + leaf 주입 가드 — leaf 주입 여부 실측 먼저 |
| PR 충돌 점검 vs main 직커밋 정책 상충 | 📋 | docs-keeper §0/harness §11/planner 정렬 — 정책 결정 |
| 워크플로우 meta↔본문 자동 가드 부재 | ✅ | `_common.ps1` 에 `Test-WorkflowPhaseDrift`(meta.phases title ↔ 본문 phase() 정규식 정합 검사) + harness-status `## 워크플로우 무결성` 섹션 호출. 현재 5/5 정합(drift 0) |
| 카탈로그 5곳 하드코딩 + 매직넘버 5 | 🟡 | inject hook 워크플로우 카탈로그를 `.claude/workflows/*.js` 동적 나열로 교체(SSOT, fallback 5종) + 매직넘버 30 을 `_common.ps1` `$ClaudeMdLineLimit` 로 단일화(size-check+harness-status 소비). 나머지 하드코딩 위치 정리는 ⏭️ |
| read-only 에이전트(explorer/planner/reviewer) Bash 권한 | 📋 | "Bash 로도 변경 금지" 명문화 또는 planner Bash 제거 |
| historian Write 도구 vs "append만" 모순 | ⏭️ | 문구 정합(없을 때만 생성) 또는 Write 제거 |
| P규칙 grep hardening 불일치(planner/explorer 박제) | ⏭️ | "예시 grep, 권위는 reviewer/conventions.md" 격하 |
| release/codebase/design round/budget cap 부재 | 🟡 | release-review MAX_VERIFY 적용. 나머지 ⏭️ |
| historian/정형 leaf 의 max effort 과지출 | 📋 | "정형 영구화 leaf 는 max 예외" 정책 |
| settings.json ultracode 활성화 키 부재 | 🔍 | 공식 키 존재 확인 후 있으면 추가, 없으면 "관례" 로 정직 표기 |
| 발견성 — skills+workflows 통합 카탈로그 부재 | ⏭️ | harness-status 에 워크플로우 이름/호출법 출력 확장 |
| 장비간 hook-errors 노출(state/ 휘발) | ⏭️ | 치명 실패는 git-tracked 위치에도 1줄 |

## Low impact

| 영역 | 상태 |
|------|------|
| §2 파일트리 memory/ 누락 | ⏭️ |
| pwsh 프로세스 스폰 오버헤드(~372ms) | 📋 인지 (추가 hook 신설 시 빈도 기준 판단) |
| pending-docs.txt 세션경계 이월 | ⏭️ |
| auto-push git commit 매칭 정확도(ahead 게이트 권장) | ⏭️ |
| reviewer/verifier dotnet build 중복 명시 | ⏭️ |
| leaf 후속추천 스키마 누락 사장 | ⏭️ |
| tools 나열 순서 6파일 제각각 | ⏭️ |
| description 라우팅 트리거 희석 | ⏭️ |
| 'workflow' 용어 중의성(ultracode vs PR절차) | ⏭️ |
| CLAUDE.md 30줄 무여유 | 📋 인지 (P1/P5 장문 셀 분리로 헤드룸 확보 가능) |

## 다음 우선순위 (제안)

1. ~~메모리 근본 해결 (#3)~~ — ✅ 완료: `Sync-Memory` hook 적용 (SessionStart C:↔E: 동기화).
2. **PreCompact hook** (medium) — ultracode 가 컴팩션 빈도를 올려 연속성 보강 가치 높음.
3. **카탈로그/매직넘버 단일화** + **meta-phase 자동 가드** — 워크플로우 추가/수정 시 drift 방지.
4. git 쓰기 경합(#4)·inject 토큰(#) — 실측 후 판단.
