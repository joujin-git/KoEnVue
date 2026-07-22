# AUDIT-2 2026-06-08 — 하네스 재점검 (harness-optimize 2회차)

`Workflow({name:'harness-optimize'})` 2회차 실행 결과(6 에이전트 병렬 + completeness critic, **53건**, 462k 토큰)의 영구 추적 문서. 1차([AUDIT-2026-06-08-harness.md](AUDIT-2026-06-08-harness.md), 42건)의 ⏭️ 항목 적용(PreCompact·카탈로그·meta-phase·매직넘버, 커밋 `f8ef8e3`) **직후** 재점검 — 새 인프라가 자기 자신을 다시 점검(dogfooding 2회차).

> 워크플로우는 docs-keeper 편집/커밋(`f8ef8e3`) **직전** 상태를 읽어 일부 항목(#6 PreCompact 문서, #7 AUDIT stale)을 미해결로 봤으나 같은 턴에 이미 해결됨 — 아래 🔄 로 표기.

상태 범례: ✅ 이번 적용 · 🔄 이미 해결(`f8ef8e3`/docs-keeper) · 🔬 검증완료(확정/반증) · 📋 보류(다음/정책) · ❌ 오판(반증됨)

## effort/ultracode 실효성 검증 (claude-code-guide, 공식 문서)

이번 재점검이 제기한 최대 불확실성("max effort + ultracode 가 실제로 켜져 있는가")을 공식 문서로 확정:

| 항목 | 결론 |
|------|------|
| `env.CLAUDE_CODE_EFFORT_LEVEL=max` | 🔬 **공식 환경변수, 최우선 경로** — effort=max 실효 확인 (env > settings > 모델기본) |
| settings `effortLevel:"max"` | 🔬 **무효값** — settings 파일에선 `xhigh` 가 최대, `max`/`ultracode` 는 session-only → ✅ `xhigh` 로 정정(실효는 env 가 max 유지) |
| ultracode 공식 모드 | 🔬 `xhigh` + dynamic workflow 오케스트레이션. **max > xhigh** 라 공식 ultracode 활성화는 effort **강등** → **현행(env max + hook workflow 유도) 유지가 effort 우월** (harness.md 논리 확증). settings.json 으로 영속 불가(session-only) |
| statusLine `effort.level` | 🔬 **실제 전달됨**(low/medium/high/xhigh/max, ultracode 는 xhigh 로 보고). 워크플로우의 "데드브랜치" 비판은 ❌ 오판 |

## 이번 세션 적용 (범위: 안전·견고성·문서정합)

| area | impact | 조치 |
|------|--------|------|
| Invoke-HookSafely hook 침묵실패 (#1) | high | ✅ `-FallbackContext`/`-EventName` 옵션 — inject 가 Write-HookOutput 전 죽어도 최소 ultracode 안내 출력. throw 시뮬 검증 OK. **ultracode 항상-ON 단일 실패점 방어** |
| scratch 보안 프로브 (#8) | high | ✅ PR-15(admin-elevation, v0.9.5.2 완결) 잔재 7파일 삭제. gitignore라 리포 영향 0, 재현은 [PR-15 설계문서](PR-15-admin-elevation.md) |
| effort env 검증 (#5) | high | 🔬✅ 위 검증 표 — `effortLevel:max`→`xhigh` 정정 |
| ultracode 앵커 (#9) | medium | 🔬 위 검증 표 — 현행 유지가 정답(공식 ultracode 는 effort 강등) |
| phase-drift 자동가드 (#22) | medium | ✅ post-edit-doc-sync 가 `.claude/workflows/*.js` 편집 시 `Test-WorkflowPhaseDrift` 호출(함수 기존). drift 즉시 경고 |
| transcript 빈 발췌 침묵 | medium | ✅ stop-record 가 text 없는 턴에 명시 마커 — 침묵 실패 가시화 |
| statusline effort 폴백 | medium/low | ✅ payload.effort.level 미수신 시 env 반영(기존 하드코딩 max) |
| harness-status scratch 카운트 | medium | ✅ scratch 파일 수 노출(보안 프로브 누적 감지) |
| 문서 정합 ~10건 | — | ✅ docs-keeper 일괄(§1 검증 박제·§12 시제·§6 숫자·§4 _common 함수목록·역할분담 3/6·§10 한계·memory 표기·scratch 반영) |

## 이미 해결 (워크플로우가 직전 상태를 봄)

| area | 비고 |
|------|------|
| PreCompact 문서 누락 (#6) | 🔄 harness.md §2/§4 추가됨 (`f8ef8e3`+docs-keeper) |
| AUDIT meta-phase/카탈로그 발견성 stale (#7) | 🔄 AUDIT 갱신됨 |

## 보류 — 다음 (impact 순)

### High (정책·완전성 — 사용자 이번 미선택)
| area | 왜 보류 | 다음 조치 |
|------|---------|-----------|
| PreToolUse 파괴명령 가드 (#2) | 새 정책 결정 | `rm -rf`·`reset --hard`·`force push`·`requireAdministrator`·`dotnet add package` 차단 hook (bypassPermissions 속도 유지, 비가역만 게이트) |
| release-review verifier 누락 (#3) | 워크플로우 완전성 | Synthesize 전 build/publish/test phase 또는 "빌드검증 안 함" note 명시 |
| budget 비대칭 (#4) | 비용가드 | release/codebase/harness/design 4개에 hard cap + 종료 note 에 `budget.spent()` |

### Medium
- ✅ autoMemoryDirectory 무실효 잔존 — **2026-07-22 처리**: 절대경로(`E:/dev/KoEnVue/.claude/memory`)로 교체, 효력은 다음 세션의 memory 경로 안내로 검증(무효여도 Sync-Memory 가 보전 → 실패 모드 안전). 같은 세션에 **Sync-Memory 자체의 치명 결함 발견·수정** — C: 디렉토리째 부재 시 `Get-AutoMemoryDir`→`$null`→skip 으로 복구 불발(= 복구가 가장 필요한 순간에 무동작). 상세 [harness.md §12](../harness.md)
- 📋 hook pwsh 콜드 오버헤드(~381ms)·auto-push every-Bash — matcher 정규식 끌어올림/timeout 축소/_common 분리
- 📋 reviewer §0 invariant '5 위치' 박제 stale — 전수 grep 단일화로 격하
- 📋 문서 동기화 매핑 4곳 중복(docs-keeper/reviewer/post-edit/harness) — 단일 진실원 포인터
- 📋 workflows 정적검증 안전망 — `Test-WorkflowMetaValidity`(meta 구조·비결정 API 리터럴 검사) harness-status 추가
- 📋 conventions.md grep 게이트 매 워크플로우 재실행 비용 — 기대값 기계가독 주석화
- 📋 워크플로우 진행/완료 장비간 연속성 — 결과 세션로그 강제 기록 or SessionStart 요약
- 🔄 harness-status 신규 점검축 — scratch 카운트 ✅ 적용, budget 미배선/매핑 실존은 📋
- 📋 bypassPermissions+auto-push main 직푸시 — 직전 SHA state 기록(revert 앵커)
- 📋 역할분담 문서 3/6 과대기술(워크플로우 호출 explorer/planner/reviewer만) — docs-keeper 정정(이번 부분반영)
- 📋 워크플로우-에이전트 출력형식(schema vs 마크다운) — 에이전트 본문에 'schema 주어지면 그것 우선' 1줄
- 📋 planner/explorer/reviewer Bash read-only 미명문화 — 'Bash 는 조회 전용' 가드
- 📋 코드 변경 PR 충돌 점검 소유자 공백 — planner 작업흐름에 `gh pr list` 추가
- 📋 숫자 '5' 3중 하드코딩(SKILL/README/harness.md) — 무수치/자기참조화
- 📋 bug-hunt vs release-review(concurrency) 중복 — 선택기준 문서화
- 📋 CLAUDE.md 30줄 무여유 — P1/P5 장문 셀 conventions.md 분리로 25줄대
- ✅ §12 split-brain 시제 모순 — docs-keeper 정정
- 📋 inject-turn-context fallback '5종' 하드코딩 — 무수치 안내로
- 📋 README `isolation?` 죽은 계약(미사용·미검증) — 주석 or 1회 사용 검증
- 📋 codebase-audit Audit phase MAX_MODULES 부재(release-review MAX_VERIFY 와 비대칭) — `modules.slice(0,20)`
- ✅ transcript JSONL 스키마 침묵 빈발췌 — 마커 적용(스키마 한계 박제는 docs-keeper)
- 📋 post-edit 매핑 docs 실존 미검증 — Test-Path 가드 or harness-status 점검
- 📋 release-review P6·버전4-part 미점검 — invariant 차원 프롬프트에 명시

### Low
- 📋 PreCompact↔Stop 세션파일 동시 append 경합 — Add-SessionBlock mutex(드문 깨짐)
- 📋 `_common.ps1` dot-source 실패 = 하네스 전체 붕괴(안전망의 안전망 메타 단일점) — dot-source try/catch + 로드 스모크
- 📋 design-compare/codebase-audit Synthesize 영속화 책임 공백 — historian 노드 or 강제 note
- ✅ effortLevel 중복소스 — xhigh 정정으로 정합(env 우선 명시)
- ❌ statusline effort 데드브랜치 — 오판(effort.level 실제 전달)
- 🔬 settings 구조정합 — 이상 없음(9 hook 참조==디스크, settings.local 부재)
- 📋 hook-errors.log hookName 추출 취약 + session-end 직접쓰기 포맷 이원화 — Write-HookError 통일
- 📋 reviewer↔verifier debug build 중복 — verifier 단일 소유
- 📋 historian Glob 부재 — tools 에 Glob 추가
- 📋 model: inherit 워크플로우 상속 미정의 — 규칙 명문화 or §10 한계
- ✅ §6 슬래시 커맨드 5/6 숫자 drift — docs-keeper 정정
- 📋 skills description 트리거 신호(plan/sync-docs 구현우선) — 의도우선 재배열
- 📋 design-compare args.feature 필수 발견성 — inject 카탈로그 표식
- 📋 harness-optimize rank[impact] NaN 정렬가드 — `?? 1` 폴백
- ✅ §10 statusline 한계 비대칭(model/effort 폴백) — docs-keeper 정정
- ✅ §4 _common 함수목록·memory 파일명 표기 — docs-keeper 정정
- 📋 statusline 'ultracode' 항상-녹색(런타임 무관) — state 타임스탬프 연동 or '주입식' 표기

## 다음 우선순위 (제안)

1. **정책 묶음** (사용자 결정 시): PreToolUse 가드(#2) + budget 비대칭(#4) — bypassPermissions·비용 안전망. 가장 impact 높은 미적용.
2. **워크플로우 완전성**: release-review verifier(#3)/P6/버전4-part — 릴리즈 게이트 공백.
3. **견고성 잔여**: `_common` 로드붕괴 가드, PreCompact↔Stop mutex.
4. **정합 잔여**: reviewer §0 박제 격하, 매핑 단일화 — drift 표면 축소.

---

## 2차 적용 — "비용상한 제외 나머지 모두" (사용자 요청, 같은 날)

위 '보류' 항목 중 budget(비용상한)만 제외하고 적용:

| area | 조치 |
|------|------|
| release-review verifier 누락 (#3, high) | ✅ `Build` phase 추가 — verifier 가 build·publish·test 실행 + BUILD_SCHEMA. invariant 차원에 버전 4-part·P6 단방향 명시 점검 |
| `_common` dot-source 로드붕괴 (low) | 🟡 harness-status 에 `_common 로드` 스모크(Invoke-HookSafely 로드 확인) 추가 — 사전 감지. 각 hook 런타임 try/catch 가드는 ROI 낮아 보류 |
| PreCompact↔Stop mutex (low) | ✅ `Add-SessionBlock`(named mutex `KoEnVue-session-md`) — pre-compact/stop-record/session-end 직렬화 |
| hookName 추출/포맷 통일 (low) | ✅ `Write-HookError` 헬퍼 — Invoke-HookSafely catch + session-end 직접쓰기 통일 |
| harness-optimize NaN 정렬 (low) | ✅ `rank[x] ?? 1` 폴백 (found/all sort) |
| inject fallback 5종 하드코딩 (medium) | ✅ 무수치 안내로 |
| historian Glob 부재 (low) | ✅ tools 에 Glob |
| planner Bash read-only (medium) | ✅ 금지사항 'Bash 도 read-only' + §1 PR 충돌 점검(gh pr list) |
| reviewer↔verifier build 중복 (low) | ✅ reviewer §2 에 'verifier 곧 호출 시 생략 가능' |
| CLAUDE.md 30줄 무여유 (medium) | ✅ Documentation map 표→산문, 31→26줄(여유 4) |
| 숫자 무수치화 (medium) | ✅ harness.md/README 워크플로우 '5종'→무수치(파일시스템 SSOT) |
| 워크플로우 선택기준 (medium) | ✅ harness.md §3 release-review vs bug-hunt 가이드 |
| 역할분담 3/6 (medium) | ✅ harness.md §3 — verifier 도 노드(release-review Build)로 승격 정정 |
| isolation 죽은계약 (medium) | ✅ README 작성주의에 '미사용·미검증' 주석 |
| reviewer §0 5위치 박제 (medium) | 🟡 이미 방어적 설계(수치 미박제, 방법 A 전수추출 우선) — 추가 불필요 판단, 유지 |

### PreToolUse 파괴명령 가드 (#2) — 도구 제약으로 차단 불가

claude-code-guide 공식 검증(code.claude.com/docs/en/hooks): **bypassPermissions 모드에서 PreToolUse 의 `permissionDecision:"deny"` 는 무시됨** — hook 은 실행되나 차단 효과 0("their decisions have no effect"). 즉 속도우선(bypass) 정책과 파괴명령 *차단* 이 Claude Code 레벨에서 양립 불가. 차선책(audit 로깅 / 권한모드 전환 / 감수)은 **사용자 결정 대기**.
