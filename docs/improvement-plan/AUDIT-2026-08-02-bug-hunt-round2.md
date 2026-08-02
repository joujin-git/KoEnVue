# AUDIT 2026-08-02 — bug-hunt 재개 (v1.0.0.0 2차 보류 근거)

**출처**: `/bug-hunt` 워크플로우 재실행 (2026-08-02, 5 라운드, 에이전트 254개 / 완료 246 · 실패 8, 서브에이전트 토큰 24.9M, 소요 107분).
**대상**: `App/` 와 `Core/` 의 2-스레드 경로 전체.
**선행**: [AUDIT-2026-07-30](AUDIT-2026-07-30-concurrency-bug-hunt.md) 의 14그룹을 전부 수정한 뒤 그 결과를 포함해 재탐색.

**결론**: v1.0.0.0 정식 배포를 **다시 보류**한다(사용자 결정, 2026-08-02). 확정 50건 중 **일부는 07-30 감사 수정분 자체의 결함**이며, 목록은 아래 §2 때문에 여전히 하한선이다.

---

## 1. 이번 세션이 드러낸 것 — 수정이 새 결함을 만들었다

2026-08-01~02 한 세션 안에서 **"수정 → 검증 → 그 수정의 결함 발견" 이 세 번 연속** 일어났다.

| 단계 | 결과 |
|---|---|
| AUDIT-2026-07-30 의 14그룹 수정 | 묶음 1~7, 커밋 9개 |
| → `/release-review` (37 에이전트) | 32건 중 **19건 확정** — 대부분 위 수정분의 결함 |
| → 19건 수정 | 커밋 `62c5848` |
| → `/bug-hunt` (254 에이전트) | **50건 확정** — 그중 일부가 다시 위 수정분의 결함 |

특히 **저장 경로(`JsonSettingsManager`)는 세 번 연속 지적**됐다:

1. §N-48 3-way 병합 도입
2. 리뷰가 4갈래 결함 확정(인메모리 미반영 · 비들여쓰기 붕괴 · 중첩 통째 비교 · 원문 비교 오판)
3. bug-hunt 가 다시 2건 확정(mtime 의미 겹침 · 파생 캐시 미전파)

**이 패턴 자체가 릴리즈 보류의 주된 근거다.** "고쳤다" 는 진술의 신뢰도가 낮은 구간이라는 뜻이기 때문이다.

## 2. 커버리지 한계 — 두 번 연속 같은 지점에서 끊겼다

**라운드 4·5 의 finder 8종이 전부 실패했다** (`hunt:shared-state#4` ~ `hunt:lifetime#5`). 워크플로우가 반환한 사유는 `You've hit your monthly spend limit` 이었다.

**다만 그 사유는 확정이 아니다.** 직후 사용자 확인 요청으로 서브에이전트를 하나 띄워 보니 **정상 동작**했고(2026-08-02, haiku 프로브 1회), 사용자도 한도에 여유가 있다고 확인했다. 즉 그 시점에 그 메시지를 받은 것은 사실이지만 **8개 finder 가 실패한 진짜 원인은 미확정**이다 — 일시적 상한, 병렬 폭주에 대한 별도 제한, 혹은 다른 원인의 오표기일 수 있다. **다음 재개 전에 이것부터 확인할 것.**

07-30 감사도 같은 지점에서 끊겼고(그때는 세션 토큰 한도), 워크플로우는 두 번 모두 그것을 "새 결함 0" 으로 읽어 dry 카운터를 올리고 종료했다 — **수렴이 아니라 자원 소진이다.** 즉 이 목록도, 07-30 목록도 하한선이며, 같은 도구를 같은 규모로 다시 돌리면 같은 자리에서 또 끊길 공산이 크다.

> **교훈**: 도구가 돌려준 실패 사유를 그대로 결론으로 옮기지 말 것. 이 세션에서 그 메시지를 근거로 "추가 워크플로우 불가" 라고 단정했으나, 저비용 프로브 한 번으로 뒤집혔다.

**다음 재개 시 조치**: 라운드 수를 3으로 낮춰 예산 안에서 완주시키거나, finder 를 나눠 여러 세션에 분산한다. 지금 구조로는 마지막 두 라운드가 항상 버려진다.

## 3. 처리 현황

| # | 상태 |
|---|---|
| N1 · N14 | ✅ **수정 완료** (`0b9d79d`) — `_lastMtime` 을 폴링 기준과 동기화 기준으로 분리 |
| N4 · N11 · N15 · N16 | ✅ **수정 완료** — 이번 세션 수정분의 결함 4건 (아래) |
| 그 외 **44건** | ◻ 미착수 (대부분 선재) → **§5 에서 20그룹으로 병합** |

> **2026-08-02 정정**: 이 표는 미착수분을 「41건」 으로 적었으나 산술이 맞지 않는다 — 확정 50건에서 수정 완료 6건을 빼면 **44건**이다. 「9건 수정 / 41건 문서화」 라고 적힌 세션 요약도 같은 오류다(실제 수정은 6건). 아래 §5 병합은 44건 기준이다.

**N4** — UI 경로(`ToggleStartupRegistration` · `ReregisterIfAdminChanged`)가 `Monitor.TryEnter(200ms)` 로 상한을 둔다. 겹치면 조작을 무시하고 `I18n.StartupTaskBusy` 로 안내 — 조용히 넘어가면 "눌렀는데 아무 일도 안 일어난다" 가 된다.
**N11** — `Initialize` 가 새 `StreamWriter` 대입 전에 기존 것을 Dispose. 락을 쥔 상태라 좀비가 쓰는 중이 아니다(그게 락 획득이 뜻하는 바).
**N15** — 저장 경로 4곳을 `SaveAndSync` 헬퍼로 통일. 병합이 실제로 일어난 경우(참조 비교)에만 프로필 캐시·I18n·감지 방식·엔진 캐시를 다시 세운다.
**N16** — `ScaleInputDialog` / `CleanupDialog` 가 자체 모달 루프를 돈 **뒤에** `currentConfig()` 를 다시 읽는다.

### 처리된 우선 대상 (이번 세션이 만들었거나 불완전하게 남긴 것) — ✅ 전부 수정

- **N11** — `StopDrainThread` 의 "writer 를 건드리지 않는다" 분기(락 타임아웃) 뒤에 `Initialize` 가 `_fileWriter` 를 새로 대입해 **옛 writer 가 누수**된다. 릴리즈 리뷰 #4 를 고치면서 생긴 반대편 구멍.
- **N4** — §N-13 으로 넣은 `_taskMutationLock` 을 UI 스레드가 잡는데, 백그라운드 schtasks 동기화가 최대 ~8초 보유한다. `Monitor.Enter` 는 메시지를 펌프하지 않으므로 **메시지 루프가 그동안 멈춘다.**
- **N15** — 릴리즈 리뷰 #1 은 `Settings.Save` 반환값을 `_config` 에 되돌리는 것까지만 고쳤다. 세 호출 지점 모두 그 대입이 마지막 문장이라 **`Overlay.HandleConfigChanged` 등 파생 캐시로 전파되지 않는다.**
- **N16** — AUDIT §B 의 `config = currentConfig()` 는 함수 진입 시 1회뿐인데, `IDM_SIZE_CUSTOM` 과 `IDM_CLEANUP` 은 그 뒤에 **스스로 중첩 모달 루프를 연다.** 그 안에서 config 가 교체되면 다시 stale 이다.

## 4. 착수 전 규칙 (07-30 §3 과 동일)

확정 판정은 finder 1 + 검증 렌즈 2표 이상이지만 **메인이 재확인하지 않았다.** 착수 전 해당 파일을 직접 읽어 전제를 확인할 것 — 07-30 작업에서 실제로 **오탐 1건(§N-60)** 과 **초벌 오판 1건(§N-39, 오탐인 줄 알았으나 실재)** 이 나왔다.

---

## 5. 병합 결과 — 미착수 44건 → 20그룹 (2026-08-02)

§6 의 원본 44건을 근본 원인 단위로 병합했다. **병합 판정은 전부 코드를 직접 열어 확인했다** — §4 규칙대로, 그리고 "이미 고쳤으니 중복일 것" 이라는 추정이 이 프로젝트에서 두 번 틀렸기 때문이다.

### 5.1 순수 중복 — 이미 닫힘 (7건, 착수 불필요)

코드 확인으로 **닫혔음이 증명된** 것만 여기 둔다. 표의 "확인" 열이 근거다.

| 원본 | 동일 결함 | 확인 |
|---|---|---|
| N22 | N1 · N14 (`0b9d79d`) | `JsonSettingsManager.cs:288` 병합 가드가 `_syncedMtime` 을 본다. `CheckReload`(:443-446)는 `_lastMtime` 만 전진 — 두 의미가 실제로 분리됨 |
| N30 | N11 (`19f4ba5`) | `Logger.cs:138-139` 가 새 writer 대입 **전에** `_fileWriter?.Dispose()` 수행 |
| N21 · N26 · N32 · N38 · N46 | N16 (`19f4ba5`) | `Tray.cs:310`(ScaleInputDialog 후) · `:689`(CleanupDialog 후) 둘 다 `config = currentConfig()` 재조회 |

### 5.2 그룹 목록 — 20그룹 (**5그룹 13건 해결** · 15그룹 24건 미해결)

**⚠ G6 은 새로 확인된 잔여 결함이다.** N28 · N31 · N47 을 "N15 수정으로 닫혔다" 로 처리하려다 코드를 열어 보니 절반만 닫혀 있었다. 병합 작업이 실제로 잡아낸 것이라 우선순위 최상단에 뒀고, **G11 과 함께 2026-08-02 에 수정 완료**했다(§5.5).

| 그룹 | 제목 | 원본 | 건수 | 영향 |
|---|---|---|---|---|
| ~~**G6**~~ ✅ | 저장 병합 후 **전이 적용자**가 재실행되지 않음 | N28 · N31 · N47 | 3 | 🔴 설정 유실·불일치 |
| ~~**G1**~~ ✅ | `log_to_file=false` 면 로그가 소비자 없는 버퍼에 무한 적재 | N3 · N9 · N20 · N36 · N50 | 5 | 🔴 메모리 상주 + 핫패스 비용 |
| **G4** | `_indicatorVisible` 이 화면과 어긋난 채 `true` 로 박제 | N7 · N17 · N25 · N37 | 4 | 🟠 불필요 IPC + 재표시 불가 |
| **G5** | `WM_CLOSE` 경로가 핸들 필드 리셋을 우회 | N10 · N40 · N43 | 3 | 🟠 죽은/재활용 HWND 에 post |
| ~~**G2**~~ ✅ | `_drainThread` 가 non-volatile + 락 밖 변경 | N2 · N24 · N35 | 3 | 🟡 로그 유실 |
| **G7** | 트레이 최초 등록만 무효 HICON 방어 누락 | N13 · N44 | 2 | 🟠 빈 트레이 아이콘 고착 |
| **G8** | `Tray.UpdateState` 가 블로킹 IPC 중 재진입 | N8 · N42 | 2 | 🟠 살아있는 HICON 파괴 |
| **G9** | 테마 변경이 커서 헤일로에만 전달 안 됨 | N33 · N49 | 2 | 🟠 색 불일치 영구 |
| **G10** | 비가시 `_hwndMain` 을 포커스 복원 대상으로 사용 | N29 · N41 | 2 | 🟠 배지 영영 숨김 |
| ~~**G3**~~ ✅ | 크래시 핸들러의 `StopDrainThread` 가 임의 스레드 재진입 | N23 | 1 | 🟠 크래시 로그 유실 |
| ~~**G11**~~ ✅ | `Save` 의 `TryLoad` 실패 분기가 조용히 병합 전 값 반환 | N19 | 1 | 🔴 사용자 편집 되돌림 |
| **G12** | `WaitForExit` 반환값 무시 → 미등록 오판 + 핸들 누수 | N12 | 1 | 🟠 중복 등록 |
| **G13** | 필터 분기가 FG 캐시를 반만 갱신 | N5 | 1 | 🟡 프로필 오적용 |
| **G14** | `WindowMoving` 래치가 config 교체를 인지 못함 | N18 | 1 | 🟠 배지 영구 숨김 |
| **G15** | DPI 변경 후 후속 Render 없이 빈 DIB 블리트 | N6 | 1 | 🟠 배지 소멸 |
| **G16** | `EnableWindow` 가 별도 top-level 배지를 막지 못함 | N39 | 1 | 🟠 모달 뒤 설정 변경 |
| **G17** | 리로드 실패 MessageBox 안에서 `HandleConfigChanged` 재진입 | N34 | 1 | 🟠 안내 무한 누적 |
| **G18** | `OnProcessExit` 의 스레드 친화성 전제가 자기모순 | N45 | 1 | 🟡 종료 정리 미수행 |
| **G19** | `user_hidden` true→false 핫리로드만 비대칭 | N48 | 1 | 🟠 배지 복원 안 됨 |
| **G20** | `CleanupDialog` 선택 항목이 stale 스냅샷 기준 | N27 잔여 | 1 | 🟠 엉뚱한 위치 삭제 |

### 5.3 그룹 상세

**G6 — 저장 병합 후 전이 적용자가 재실행되지 않음** (N28 · N31 · N47)
`Program.cs:1092-1104` 의 `SaveAndSync` 는 병합 발생 시 `ClearProfileCache` · `I18n.Load` · `UpdateDetectionMethod` · `Overlay.HandleConfigChanged` **4가지만** 다시 세운다. 그런데 `HandleMenuCommand` 람다(`:1156-1197`)는 그 앞에서 `ApplyCursorConfigChange()`(:1177) · `ApplyUserHiddenTransition`(:1182) · `ShowIndicatorAtForeground`/`UpdateColor`(:1188/1191) · `ApplyTrayEnabledTransition`(:1194) 를 **병합 전 값으로** 실행하고, `SaveAndSync` 는 마지막 문장(:1196)이다. `Logger.SetLevel`/`Initialize` 는 양쪽 어디에도 없다. 헬퍼의 doc comment(:1088-1089)가 "커서 lifecycle·표시 전이는 호출자마다 맥락이 달라 여기서 하지 않는다(각 호출자가 자기 전이 판정으로 이미 수행)" 라고 적었지만, **그 판정 자체가 병합 전 값 위에서 끝난 뒤**라 전제가 성립하지 않는다. `Save` 의 mtime self-bump 로 핫리로드도 차단돼 자기치유가 없다.
*재현*: `config.json` 에서 `cursor_indicator_enabled: false` 로 편집 → 5초 폴링 전에 트레이 메뉴에서 투명도 변경 → `_config` 와 디스크는 `false`, 그러나 커서 헤일로는 켜진 채 남는다.
*방향*: 전이 적용자를 병합 후 재실행 가능한 형태로 분리하거나, 람다에서 `Save` 를 **먼저** 호출해 적용자가 병합 결과 위에서 돌게 한다(형제 경로 `HandleTrayToggle` 은 이미 Save 가 먼저다 — 순서 불일치 자체가 결함의 증거).

**G1 ✅ — 파일 로깅 OFF 가 "드롭" 이 아니라 "무한 보류"** (N3 · N9 · N20 · N36 · N50)
`Logger.cs:228` 의 `if (_drainThread is null)` 이 **pre-init 과 로깅 비활성을 구분하지 못한다.** `_drainThread` 가 영구 null 이 되는 경로가 셋: `Initialize(enabled:false)` 조기 반환(:103), writer 락 타임아웃(:113-118), `StreamWriter` 생성 실패(:144-151). 이후 모든 스레드의 모든 로그가 `_preInitBuffer` 로 가는데 소비자는 `Initialize` 성공 끝의 `FlushPreInitBuffer` 뿐이다. 감지 루프가 80ms 주기라 `log_level=debug` 조합에서 수십 초 만에 상한(10,000)에 닿고, 이후 호출마다 축출 루프를 돈다.
*방향*: "파일 로깅 비활성" 을 별도 상태로 두고 그 경우 큐에 넣지 않고 버린다.

**G4 — `_indicatorVisible` 거짓 true** (N7 · N17 · N25 · N37)
`ShowIndicatorAtForeground`(`Program.cs:599`)가 `_indicatorVisible = true` 를 **먼저** 세우고 `Animation.TriggerShow` 를 부르는데, NonKorean + `NonKoreanImeMode.Hide`(기본값) 가드(`Animation.cs:86-89`)가 `TriggerHide(forceHidden:true)` 로 빠지고, `OverlayAnimator.TriggerHide` 첫 줄(`:296`)이 `_phase == Hidden` 이면 즉시 return 해 `_onHide()` → `onHidden` 훅이 발화하지 않는다. `_phase` 초기값이 `Hidden` 이라 **부팅 후 첫 NonKorean 알림에서 바로 성립**한다. `HandlePositionUpdated`(`:674`)도 같은 선-대입 패턴이다. 이 플래그는 감지 스레드가 읽는 유일한 가시성 계약이라, 거짓 true 는 매 틱 불필요한 `WM_HIDE_INDICATOR` 를 유발하고 `wasHidden` 재표시 판정을 무력화한다.
*방향*: 플래그를 `onHidden`/`onShown` 훅에서만 갱신하도록 단일화하거나, `TriggerShow` 의 Hide 가드 경로가 훅을 반드시 발화시키게 한다.

**G5 — `WM_CLOSE` 가 §N-42 invariant 를 우회** (N10 · N40 · N43)
`WndProcCore` 에 `WM_CLOSE` case 가 없어 `DefWindowProcW` 가 `DestroyWindow(_hwndMain)` 을 수행하는데, 뒤따르는 `WM_DESTROY` 는 `PostQuitMessage(0)` 만 하고 핸들 필드를 Zero 로 내리지 않는다. §N-42 의 필드 리셋은 `OnProcessExit` 한 곳에만 있다. **트레이 「관리자 권한」 토글(`Tray.cs:359`)이 이 경로를 정상 동작으로 탄다** — 예외 경로가 아니다.
*방향*: `WM_CLOSE` case 를 추가해 파괴 전에 세 핸들 필드를 Zero 로 내린다.

**G2 ✅ — `_drainThread` 가시성** (N2 · N24 · N35)
`Logger.cs:22` 는 여전히 `private static Thread? _drainThread;` — 같은 파일의 `_generation` 은 `Volatile.Read`/`Interlocked` 로 다루는데 이것만 누락이다. 라우팅 스위치로 모든 스레드가 읽고 메인이 `Initialize`/`StopDrainThread` 에서 **락 밖**으로 쓴다. `StopDrainThread`(:364-365)의 read-then-clear 도 비원자이며, G3 의 경로로 다른 스레드에서 진입 가능하다.

**G20 — `CleanupDialog` 선택 항목 매핑** (N27 잔여)
N16 수정으로 커밋 베이스는 `currentConfig()` 재조회(`Tray.cs:689`)로 닫혔으나, `displayItems`/`originalNames` 는 **다이얼로그 열기 전 스냅샷**에서 계산된 채 남는다. 코드 주석(:688)은 "사용자가 화면에서 고른 항목이라 그대로 쓴다" 로 의도적 선택임을 밝히지만, 그 사이 리로드가 `indicator_positions` 를 바꿨다면 선택이 더 이상 존재하지 않는 항목을 가리킬 수 있다. **의도적 결정이 옳은지 판단이 필요한 항목** — 오탐일 수도 있다.

나머지 그룹(G3 · G7 ~ G19)은 각 원본 항목이 §6 에 그대로 있고 병합으로 달라진 것이 없으므로 여기서 반복하지 않는다.

### 5.4 착수 순서 제안

1. ~~**G6 · G11**~~ — ✅ **2026-08-02 수정 완료** (§5.5). 둘 다 저장 경로이고 사용자 편집 유실 계열이라 같이 다뤘다 — 이 자리는 한 세션에 네 번 고쳐진 곳이라 개별 수정이 또 서로의 구멍을 만들 위험이 가장 컸다.
2. ~~**G1 · G2 · G3**~~ — ✅ **2026-08-02 수정 완료** (§5.5). 셋 다 `_drainThread` 필드 하나에 역할이 둘(Join 대상 + 라우팅 스위치) 얹혀 있던 데서 나왔다.
3. **G5 · G10 · G16 · G17** — 창 lifecycle / 모달 계약. 서로 전제를 공유한다. ← **다음 후보**
4. **G4 · G14 · G19** — 배지 가시성 상태 기계. 셋 다 "숨겨졌는데 복원 경로가 없다" 는 같은 축이다.
5. 나머지는 독립적이라 순서 무관.

### 5.5 수정 기록 (2026-08-02)

#### 저장 경로 — G6 · G11

**G11 — `Save` 의 되읽기 실패 분기** (`Core/Config/JsonSettingsManager.cs`)
병합 후 `TryLoad` 가 실패하면 `_lastMtime`·`_syncedMtime`·`_lastPersistedJson` 세 표식을 함께 물린다. 기준선은 **호출자가 실제로 들고 있는 값**(`rawJson`)으로 되돌려야 다음 diff 가 "앱이 이번에 바꾼 것" 만 집어내고, 폴링 기준까지 내려야 self-bump 가 취소돼 5초 폴러의 핫리로드가 자기치유 경로로 열린다. Warning 로그도 남긴다 — 종전에는 완전히 침묵했다.

**G6 — 전이 적용의 단일화** (`Program.cs`, `Program.OverlayDrag.cs`)
`HandleConfigChanged` 의 적용부를 `ApplyConfigTransition(prev, next)` 로 추출하고, `SaveAndSync` 가 병합 발생 시 이를 호출하도록 승격했다. **호출자 규율에 의존하던 구조를 바꾼 것이 핵심이다** — 개별 호출자에 빠진 적용자를 채워 넣는 방식이었다면 다섯 번째 구멍이 생겼을 것이고, 실제로 이 자리는 그런 식으로 네 번 고쳐졌다. 부수적으로 `SaveAndSync` 의 반환값을 없애고 `_config` 를 직접 갱신하게 해, 릴리즈 리뷰 #1 의 정체였던 "반환값 미대입" 함정 자체를 제거했다.

**검증**
- `SaveMergeTests` 3종 신설 — 되읽기 실패 후 다음 저장이 편집을 지키는가 · 핫리로드 자기치유가 열리는가 · **정상 경로에서는 self-bump 가 유지되는가**(반대 방향 회귀 가드). tests 212 → **215**.
- **대조군 실측** — 표식 물리기를 제거한 상태에서 앞의 두 테스트가 실제로 실패함을 확인했다: `snap_gap_px` 가 `42 → 10` 으로 되돌아가고(사용자 편집 유실 재현), `CheckReload()` 가 `false`(자기치유 차단). 세 번째 테스트는 양쪽에서 통과 — 수정과 무관한 정상 경로를 지킨다는 뜻이다.
- **G6 는 단위 테스트가 불가능하다** (Program.cs 의 `private static`, Win32 강결합). 대신 [conventions.md](../conventions.md) P6 invariant 에 grep 2줄을 박았다 — `Settings.Save(` 가 **1** (SaveAndSync 단일 진입점), `ApplyConfigTransition(` 이 **3** (정의 1 + 진입점 2). 우회 경로가 생기면 게이트가 잡는다.
- 빌드 debug 경고 0 · AOT publish 성공.

**남은 검증** — 실기 확인은 못 했다. `config.json` 에서 `cursor_indicator_enabled` 를 끄고 5초 안에 트레이 메뉴에서 투명도를 바꿔, 헤일로가 실제로 꺼지는지 눈으로 봐야 한다.

#### Logger — G1 · G2 · G3

셋 다 뿌리가 하나다 — **`_drainThread` 필드에 역할이 두 개 얹혀 있었다.** Join 대상 참조이면서 동시에 로그 라우팅 스위치였고, 세 결함이 전부 그 겸직에서 나왔다. 그래서 라우팅 역할을 `FileLogRoute { PreInit, Queue, Drop }` enum(`volatile _route`)으로 분리하는 것이 수정의 중심이다.

- **G1** — `Initialize(enabled:false)` · writer 락 타임아웃 · `StreamWriter` 생성 실패 세 경로가 모두 `Drop` 을 세운다. 종전에는 셋 다 `_drainThread` 를 null 로 남겨 `EnqueueToFile` 이 "아직 부팅 중" 으로 읽었고, 비우는 주체가 없는 `_preInitBuffer` 에 1만 개까지 쌓였다.
- **G2** — `_route` 는 `volatile` 이고 `EnqueueToFile` 이 **한 번만 읽어 로컬에 담는다**(판정과 동작이 다른 상태를 보면 안 된다). `_drainThread` 도 `Volatile.Write` / `Interlocked.Exchange` 로만 접근 — 후자가 `StopDrainThread` 의 read-then-clear 를 원자화해 두 경로가 겹쳐도 Join 대상을 정확히 한 번만 집는다.
- **G3** — 자기-Join 회피(`thread == Thread.CurrentThread`), `Monitor.IsEntered(_writerLock)` 으로 락 재진입 감지 후 flush 건너뛰기, `_lifecycleLock` 으로 `Initialize` ↔ `Shutdown` 직렬화. 락 순서는 항상 `_lifecycleLock` → `_writerLock` 이고 drain 스레드는 후자만 쓰므로 데드락 경로가 없다. 크래시 핸들러가 막히지 않도록 양쪽 다 `TryEnter` + 1초 상한.

**검증**
- `LoggerReinitTests` 3종 신설 (215 → **218**) — 꺼진 동안의 로그가 파일에 새지 않는가 · 꺼진 동안 버퍼가 자라지 않는가 · 초기화 실패 시에도 자라지 않는가. 세 번째는 경로 자리에 같은 이름의 디렉토리를 만들어 `StreamWriter` 실패를 **결정적으로** 유도한다.
- **대조군 실측** — `Drop` 을 `PreInit` 과 같게 되돌리자 3종 모두 실패: 꺼진 동안의 로그가 파일에 나타나고, 버퍼가 0 → 200 으로 자랐다. 기존 4종은 양쪽에서 통과(회귀 없음).
- **G2·G3 는 결정적 단위 테스트가 불가능하다** — 크래시 핸들러를 drain 스레드 위에서 재현하려면 그 스레드가 `_writerLock` 을 쥔 채 예외를 던지게 만들어야 한다. invariant grep 1줄(`if (_drainThread is null)` = 0)이 라우팅 판정의 회귀만 잡는다.
- 빌드 debug 경고 0 · AOT publish 성공.

**남은 검증** — 실기 확인 미수행. `log_to_file` 을 껐다 켜며 작업 관리자의 메모리가 더 이상 차오르지 않는지, 다시 켰을 때 묵은 줄이 쏟아지지 않는지 보면 된다.

---

## 6. 확정 50건 상세

아래는 워크플로우가 반환한 원문을 정리한 것이다. 중복 제거 키가 `파일 + kind + desc 앞부분` 이라 **같은 결함이 다른 서술로 여러 번 잡혀 있을 수 있다** — 07-30 때 67건이 고유 14그룹이었던 것과 같은 성질이므로, 착수 시 먼저 병합할 것.

### N1 — `Core/Config/JsonSettingsManager.cs` (race)

**위치**: CheckReload (line 407-432, `_lastMtime = mtime;` line 420) vs MergeOntoDiskIfChanged fast-path (line 263) — partner field `_lastPersistedJson` (line 6

`_lastMtime` and `_lastPersistedJson` form a two-field invariant: `_lastMtime` must be the mtime of the content that `_lastPersistedJson` represents. `MergeOntoDiskIfChanged` line 263 (`if (diskMtime == _lastMtime) return nextJson;`) relies on exactly that invariant to decide "disk is what we last saw, overwriting loses nothing" and skip the 3-way merge. But two paths advance `_lastMtime` WITHOUT touching `_lastPersistedJson`: (a) `CheckReload` line 420 — this runs on the DETECTION thread (Settings.CheckConfigFileChange, ~every 5s), and (b) `TryLoad`'s failure path line 150. `_mtimeLock` makes each write atomic but does not preserve the invariant, so after either path the merge guard is silently disarmed and the next `Settings.Save` overwrites the user's on-disk edits with the in-memory config — the exact loss §N-48 / 확정 #1·#3·#5 exist to prevent. Note also that only the success path of 

**실패 시나리오**: User edits config.json in an editor that briefly holds the file open. (1) Detection thread's 5s poll → `CheckReload()` sees the new mtime T_user, sets `_lastMtime = T_user`, posts WM_CONFIG_CHANGED. (2) Main thread dispatches it → `HandleConfigChanged` → `Settings.TryLoad` throws IOException (editor still holds the handle) → caught at line 141, failure path sets `_lastMtime = T_user` again, `_lastPersistedJson` still holds the PRE-edit app state, `HandleConfigChanged` returns early keeping old settings. (3) Editor releases the file; content on disk is now valid and contains the user's edit. (4) User drags the floating badge or clicks a tray menu item → `Settings.Save` → `MergeOntoDiskIfChang


### N2 — `Core/Logging/Logger.cs` (visibility)

**위치**: `private static Thread? _drainThread` (line 22); written at lines 153/158 and 356-357, read as a routing switch at line 220 in EnqueueToFile

`_drainThread` is a plain reference field, but `EnqueueToFile` (line 220) uses it as the routing decision for EVERY log call from EVERY thread: non-null → `_logQueue` (drained to file), null → `_preInitBuffer` (only flushed by a future `Initialize`). The main thread flips it during `Logger.Initialize` on config reload: `StopDrainThread` nulls it (line 357), then `Initialize` assigns the new thread (line 153). With no volatile/lock, a detection-thread `Logger.X` can observe either stale value. Additionally `StopDrainThread`'s `Thread? thread = _drainThread; _drainThread = null;` is a non-atomic read-then-clear, and it is reachable from a non-main thread via the `AppDomain.UnhandledException` handler (Program.cs:159 `Logger.Shutdown()`, which runs on whichever thread threw — detection / UpdateChecker / StartupPathSync) concurrently with a main-thread `Initialize`.

**실패 시나리오**: User changes `log_file_path` in config.json. Main thread: HandleConfigChanged → `Logger.Initialize` → `StopDrainThread` sets `_drainThread = null`, then line 153/158 assigns and starts the new thread, then line 162 `FlushPreInitBuffer()` moves whatever is in `_preInitBuffer` into `_logQueue`. A detection-thread `Logger.Debug` that reads a stale `null` for `_drainThread` AFTER FlushPreInitBuffer has already run appends to `_preInitBuffer`, which nothing drains again until the next `Logger.Initialize` — typically never for the rest of the session, so that line is permanently missing from koenvue.log. The mirror case (stale non-null read after StopDrainThread nulled it) strands the line in `_lo


### N3 — `Core/Logging/Logger.cs` (robustness)

**위치**: Initialize early-returns at line 103 (`if (!enabled) return;`) and line 117 (TryEnter failure) after StopDrainThread already nulled _drainThread; Enqu

`_preInitBuffer` was designed as a boot-time staging area drained once by `Initialize`. But `EnqueueToFile` selects it purely on `_drainThread is null`, and there are two steady-state ways to end up permanently in that state after boot: (a) the user sets `log_to_file: false` at runtime — `Initialize` calls `StopDrainThread` (which nulls `_drainThread`) and then returns at line 103; (b) `Monitor.TryEnter(_writerLock, 1000)` fails and `Initialize` returns at line 117. From then on every single log call from every thread — including the detection loop's ~12 calls/sec — runs the cap loop at line 223 (`while (_preInitBuffer.Count >= MaxQueueSize && TryDequeue) Interlocked.Increment(...)`) and enqueues into a buffer that has no drain. `FlushPreInitBuffer` is only ever called from `Initialize` (line 162), so the buffer is not a transient.

**실패 시나리오**: User turns off `log_to_file` in config.json to stop disk writes. From that moment the process permanently retains up to 10,000 formatted log strings in `_preInitBuffer`, and the detection thread pays a ConcurrentQueue Count + TryDequeue + Interlocked.Increment on its 80ms hot path forever. If the user later re-enables `log_to_file`, `Initialize` → `FlushPreInitBuffer` dumps up to 10,000 lines that are hours old into koenvue.log ahead of any fresh line, so the file's timestamps jump backwards by hours at the re-enable point.


### N4 — `App/Startup/StartupTaskManager.cs` (robustness)

**위치**: `lock (_taskMutationLock)` at line 102 (ToggleStartupRegistration, WM_COMMAND → main thread) and line 354 (ReregisterIfAdminChanged, main thread) vs l

`_taskMutationLock` is acquired on the UI thread. `ToggleStartupRegistration` is reached from WM_COMMAND (Tray.cs:332) and `ReregisterIfAdminChanged` from WM_COMMAND (Tray.cs:346) — both run inside `WndProcCore`, on the stack of `TrackPopupMenu`'s modal loop. The background `SyncStartupPathCore` (started at MainImpl step 9c, Program.cs:333) holds the same lock across `QueryRegisteredTask()` (schtasks /query, `WaitForExit(3000)`) plus, when out of sync, `RegisterStartupTaskWithXml` → `RunSchtasks` (`WaitForExit(5000)`) — up to ~8s. `Monitor.Enter` does not pump messages, so the main thread's message loop is fully stalled for that duration. The XML doc-comment at line 35-37 explicitly reasons that read-only paths must not lock because "수정 중 UI 가 멈추면 안 되고" — but the mutation paths do exactly that, on the UI thread.

**실패 시나리오**: User launches KoEnVue (e.g. after moving the exe, so `SyncStartupPathCore` will find the path out of sync and re-register). Within the first second they right-click the tray icon and click "시작 프로그램 등록". `ToggleStartupRegistration` blocks in `Monitor.Enter` at line 102 while the background thread runs schtasks /query (up to 3s) then schtasks /create (up to 5s). For those seconds the message loop never returns: the popup menu stays painted and frozen, WM_TIMER-driven fade/highlight/CAPS/cursor-halo animations stop mid-frame, and the detection thread's WM_HIDE_INDICATOR / WM_POSITION_UPDATED posts pile up in the queue and all replay in a burst when the lock is finally acquired. It reads as an a


### N5 — `App/Detector/DetectionService.cs` (race)

**위치**: TryHandleFilter (line 232, 275) ↔ UpdateForegroundProcessCache (line 292-309) ↔ TrackWindowMove (line 386-411)

필터 분기 2곳(포인터 suppress: 232, 확정 필터: 275)이 state.LastHwndForeground 를 새 FG 로 갱신하면서 LastForegroundProcessName / LastWindowFrame 은 그대로 둔다. 그런데 UpdateForegroundProcessCache 는 'hwndForeground == state.LastHwndForeground' 하나만 보고 조기 반환하므로(294-295), 필터가 풀린 뒤에도 두 필드가 영원히 이전 앱 값으로 남는다. 재현 근거(기본 설정, position_mode=window 가 AppConfig.cs:128 디폴트 + Shell_TrayWnd 가 DefaultConfig.DefaultSystemHideClasses 포함): 1) 앱 A 포커스 → LastHwndForeground=A, LastForegroundProcessName="A", LastWindowFrame=A프레임. 2) 작업 표시줄 버튼을 클릭해 앱 B 로 전환. 클릭 직후 커서는 아직 Shell_TrayWnd 위 → IsPointerOverSuppressSurface=true → line 221-238 분기: LastHwndForeground=B 로 갱신, 이름/프레임은 A 그대로, LastFiltered=true. 3) 커서가 B 창으로 들어옴 → 미필터. UpdateForegroundProcessCache 는 B==LastHwndForeground 라 즉시 return(false) → 이름="A", LastWindowFrame=A프레임 유지. foregroundChanged=(false||LastFiltered)=true 라 TrackWindowMove 는 line 391 에서 early-return, LastWindowFrame 갱신 기회도


### N6 — `App/UI/Overlay.cs` (lifetime)

**위치**: Overlay.HandleDpiChanged (line 121-127) ← Program.SystemEvents.cs:76-82 (WM_DPICHANGED), :25-29 (PBT_APMRESUMESUSPEND)

Overlay.HandleDpiChanged 는 _engine.HandleDpiChanged() + PrepareResources() 만 호출한다. LayeredOverlayBase.HandleDpiChanged(345-360) 가 InvalidateDpiCaches() 로 _currentWidth/Height=0, _lastRenderedStyle=null 을 찍고, 뒤이은 PrepareResources → EnsureResources → EnsureDib(631-653) 가 **새 DIB 섹션(전 픽셀 0)** 을 만들어 _memDC 에 select 한다. PrepareResources 는 그리기를 하지 않으므로 이 시점의 _memDC 내용은 완전 투명한 빈 비트맵이다. HandleDisplayChange·HandleSettingChange·HandleConfigChanged 는 직후 RefreshVisibleIndicator() 로 Render 를 유발해 이 창을 닫지만, HandleDpiChanged(WM_DPICHANGED, line 76-82) 와 HandlePowerResume(line 25-29) 두 경로에는 그 후속 Render 가 없다. 그 상태에서 Render 없이 블리트만 하는 애니메이션 프레임 — UpdateAlpha / UpdatePosition / UpdateScaledSize(LayeredOverlayBase 310-338) — 이 먼저 돌면 빈 DIB 가 UpdateLayeredWindow 로 올라가 배지가 통째로 사라진다. 재현 근거(app.manifest 가 PerMonitorV2 라 WM_DPICHANGED 실제 수신): display_mode=always 로 배지 가시 상태 → 배지/오버레이 창이 DPI 가 다른 모니터로 넘어가 WM_DPICHAN


### N7 — `Program.cs` (visibility)

**위치**: ShowIndicatorAtForeground (line 596-602) / HandlePositionUpdated (line 674) → App/UI/Animation.cs:86-90 → Core/Animation/OverlayAnimator.cs:296

_indicatorVisible=true 를 Animation.TriggerShow 호출 **전에** 무조건 세우는데, TriggerShow 의 NonKorean 가드는 렌더 없이 TriggerHide(forceHidden:true) 로 빠진다(Animation.cs:86-90). 그리고 OverlayAnimator.TriggerHide 의 첫 줄이 `if (_phase == AnimPhase.Hidden) return;`(OverlayAnimator.cs:296) 이라, 이미 Hidden 상태였으면 _onHide() 가 호출되지 않는다 → Animation.Initialize 가 배선한 onHidden 훅(Program.cs:326-327, §N-34 의 안전망)이 발화하지 않는다. 결과: 화면에는 아무것도 없는데 _indicatorVisible 이 true 로 박제된다 — §N-34 가 닫은 것과 정확히 같은 불변식 위반의 다른 경로. 재현 근거(기본값 non_korean_ime=Hide, AppConfig.cs:77): 1) 배지가 숨김 상태(_phase=Hidden, _indicatorVisible=false)에서 일본어/중국어 IME 앱으로 포커스 이동. 2) 감지 스레드가 WM_POSITION_UPDATED post → HandlePositionUpdated: wasHidden=true 라 line 674 에서 _indicatorVisible=true, 이어 Animation.TriggerShow(state=NonKorean). 3) NonKorean+Hide → TriggerHide(forceHidden:true) → _phase==Hidden 조기 반환 → onHidden 미발화. 4) 이후 _indicatorVisible 은 true 로 남아, ① HandlePosit


### N8 — `App/UI/Tray.cs` (reentrancy)

**위치**: Tray.UpdateState (line 214-218) — _notifyIcon.UpdateIconAndTooltip → Shell_NotifyIconW(NIM_MODIFY)

UpdateState 는 `UpdateIconAndTooltip(newIcon…)`(214) → `_currentIcon?.Dispose()`(217) → `_currentIcon = newIcon`(218) 순서로 도는데, 214 의 Shell_NotifyIconW(Core/Tray/NotifyIconManager.cs:135)는 explorer 로의 **블로킹 크로스프로세스 SendMessage** 라 호출 스레드가 그 동안 non-queued(sent) 메시지를 계속 디스패치한다. WndProcCore(Program.cs:491-495)는 WM_SETTINGCHANGE / WM_THEMECHANGED 를 HandleSettingChange 로 보내고, 그 끝(Program.SystemEvents.cs:72-73)이 다시 Tray.UpdateState 를 부른다 → 같은 스레드 재진입. 재현 근거: 한/영 전환으로 HandleImeStateChanged → Tray.UpdateState(외부 프레임) 진입, 214 에서 블록 중에 시스템이 WM_SETTINGCHANGE(HWND_BROADCAST, SendMessageTimeout — 테마/개인 설정/정책/환경변수 변경 시 발생)를 보낸다. - 내부 프레임: newIcon2 생성 → NIM_MODIFY(shell 이 newIcon2 를 표시) → `_currentIcon?.Dispose()` 로 원래 아이콘 O 해제 → `_currentIcon = newIcon2`. - 외부 프레임 복귀: line 217 이 `_currentIcon`(= 지금 shell 이 그리고 있는 newIcon2)을 Dispose → 살아 있는 HICON 파괴. line 218 이 `_currentIcon = newIcon`(외부 아이콘)로 덮어써 newIc


### N9 — `Core/Logging/Logger.cs` (lifetime)

**위치**: Initialize (line 103) / EnqueueToFile (line 218-227) / FlushPreInitBuffer (line 165-179)

Initialize(enabled:false) 는 StopDrainThread() 로 _drainThread=null 을 만든 뒤 line 103 에서 그대로 반환한다. 그러면 EnqueueToFile 의 `if (_drainThread is null)`(line 220) 분기가 **영구히** 참이 되어, 이후 모든 로그가 _preInitBuffer 로만 쌓인다. _preInitBuffer 를 비우는 곳은 FlushPreInitBuffer 하나뿐이고 그것은 Initialize 의 enabled=true 경로 끝(line 162)에서만 불린다. 재현: config.json 의 `log_to_file` 을 false 로 두거나(AppConfig.cs:112 디폴트 true → 사용자가 끄는 정상 설정) 실행 중 false 로 핫리로드하면(Program.cs:908-913 이 Logger.Initialize 재호출) 그 시점부터 - 메인 스레드(WndProc/HideOverlay 는 호출마다 Logger.Info), 감지 스레드(DetectionService 백오프/필터 로그), WinEvent 콜백, UpdateChecker 스레드가 모두 _preInitBuffer 로 enqueue, - MaxQueueSize(10,000) 도달 후엔 매 로그 호출마다 `while (_preInitBuffer.Count >= MaxQueueSize && TryDequeue)` + Interlocked.Increment 가 돌아 1만 건을 상주 보유(수 MB)한 채 영구히 버려진다. 파일 로깅을 끈 사용자는 로그가 안 남는 것은 의도대로지만, 메모리는 계속 잡고 있고 다시 켜는 순간 1만 건의 오래된 메시지가 한꺼번에 flush 된다. 부수적으로 _drainThread 는 volatile 이 아니라 lin


### N10 — `Program.cs` (lifetime)

**위치**: WndProcCore — no `WM_CLOSE` case (switch at 427–583, falls to `default:` → `DefWindowProcW` at 581); `WM_DESTROY` at 509–512. Trigger: App/UI/Tray.cs:

`WndProcCore` never handles `WM_CLOSE`, so it reaches `DefWindowProcW`, which calls `DestroyWindow(hwnd)`. The `WM_DESTROY` case only does `if (hwnd == _hwndMain) PostQuitMessage(0)` — it never clears `_hwndMain`. The three volatile handle fields are zeroed in exactly two places (Program.Bootstrap.cs:272/277/282 and Program.Timers.cs:39/58), neither of which is on this path, so AUDIT §N-42's stated invariant ("세 필드 모두 volatile 이라 다른 스레드가 즉시 Zero 를 관측하고 자기 가드에 걸린다") is defeated whenever the window dies via WM_CLOSE.

**실패 시나리오**: Tray right-click → 「관리자 권한」 토글 → 확인. `Tray.HandleMenuCommand` IDM_ADMIN_ELEVATION does `User32.PostMessageW(hwndMain, Win32Constants.WM_CLOSE, ...)` (Tray.cs:351). WndProcCore has no WM_CLOSE case → DefWindowProcW destroys `_hwndMain`, WM_DESTROY posts WM_QUIT, but `_hwndMain` keeps the dead value. Until `OnProcessExit` runs, the detection thread keeps calling `User32.PostMessageW(host.GetHwndMain(), …)` (DetectionService.cs:176/229/272/337/353/422/432/440) and `Settings.CheckConfigFileChange(host.GetHwndMain())` on a destroyed — and kernel-recyclable — HWND. Then the entire OnProcessExit teardown runs against it: `WTSUnRegisterSessionNotification(_hwndMain)` (line 244), `KillTimer(_hwndMain


### N11 — `Core/Logging/Logger.cs` (lifetime)

**위치**: Initialize line 133 (`_fileWriter = new StreamWriter(_filePath, append: true, …)`) vs StopDrainThread's lock-timeout branch at 403–417

`StopDrainThread`'s `Monitor.TryEnter(_writerLock, 1000)` failure branch deliberately leaves `_fileWriter` non-null and undisposed ("leaving writer untouched"), but `Initialize` then assigns `_fileWriter = new StreamWriter(...)` with no null check and no Dispose of the incumbent. The §C fix closed the crash but left the writer with no owner: nothing ever disposes that instance.

**실패 시나리오**: Zombie drain thread is stuck in the rotation I/O (`File.Move`/`File.Delete` blocked by an AV scanner or a tail viewer) holding `_writerLock`. User edits `log_max_size_mb` in config.json → `HandleConfigChanged` (Program.cs:908–913) → `Logger.Initialize` → `StopDrainThread` Join(3000) times out, `Monitor.TryEnter(1000)` times out → writer left alive. The zombie then finishes its I/O and releases the lock; `Initialize`'s own `Monitor.TryEnter(_writerLock, 1000)` (line 113) now succeeds and line 133 overwrites the still-open `StreamWriter`. Because `StreamWriter`'s default share mode is FileShare.Read, opening the same `_filePath` while the orphan holds it throws `IOException` → caught at 136 → 


### N12 — `App/Startup/StartupTaskManager.cs` (robustness)

**위치**: IsStartupRegistered 76–82; QueryRegisteredTask 321–329. Called per tray right-click from App/UI/Tray.Menu.cs:204

`proc.WaitForExit(SchtasksQueryTimeoutMs)`'s bool return is discarded, then `proc.ExitCode` is read; on timeout that throws `InvalidOperationException`, which the method's own filter swallows as "not registered". The child schtasks process is never killed, and the two fire-and-forget `ReadToEndAsync()` tasks (`_ = proc.StandardOutput.ReadToEndAsync();`) own the redirected pipe FileStreams — because `StandardOutput`/`StandardError` were touched in sync mode, `Process.Close()` (invoked by `using var proc`) deliberately does not close them, so the pipe SafeFileHandles survive past Dispose until finalization.

**실패 시나리오**: Task Scheduler service is slow/hung (common right after logon, or under a domain policy refresh). `Tray.ShowMenu` calls `IsStartupRegistered()` on every right-click → the UI thread blocks 3 s inside the tray-menu build, `WaitForExit` returns false, `proc.ExitCode` throws, the catch returns false, so the menu shows 「시작 프로그램」 unchecked even though the task exists — and clicking it then runs `ToggleStartupRegistrationCore`'s else-branch, re-creating a task that already exists. Each such call also orphans one schtasks.exe and two pipe handles. Repeats on every menu open.


### N13 — `App/UI/Tray.cs` (lifetime)

**위치**: Initialize 127–131 (no IsInvalid guard) vs UpdateState 200–212 (guard present)

`TrayIcon.CreateIcon` returns `new SafeIconHandle(IntPtr.Zero, ownsHandle: false)` on GDI failure (TrayIcon.cs:107 and :138). `UpdateState` explicitly guards this (`if (newIcon.IsInvalid) { … keeping previous icon … }`) — the comment there states why a NULL HICON must never reach the shell — but `Initialize` has no such guard: it stores the invalid handle in `_currentIcon` and passes `_currentIcon.DangerousGetHandle()` (= NULL) straight to `NotifyIconManager.Add` with `NIF_ICON` set. Note `TrayIcon.CreateIcon` also never checks `CreateCompatibleDC`'s result (TrayIcon.cs:90), which is the exact failure mode `LayeredCursorBase`/`LayeredOverlayBase` treat as fatal.

**실패 시나리오**: GDI object exhaustion (another app leaking handles, or session-wide GDI pressure) at boot, or at `Tray.Recreate` after an Explorer restart (`HandleTaskbarCreated` → `Recreate` → `Remove` + `Initialize`): `CreateCompatibleDC` returns NULL → `CreateCompatibleBitmap(0,…)` returns NULL → `CreateIconIndirect` fails → invalid SafeIconHandle. `Initialize` registers it anyway, so the shell draws an empty tray slot, and `_currentIcon` is a non-owning zero handle. Nothing retries icon creation — `_addPending` is only set when NIM_ADD itself fails, which it does not here — so the tray stays blank until the next IME transition or config change happens to call `UpdateState`.


### N14 — `Core/Config/JsonSettingsManager.cs` (race)

**위치**: CheckReload() line 417-421 vs MergeOntoDiskIfChanged() line 263 (`if (diskMtime == _lastMtime) return nextJson;`)

`_lastMtime` 은 두 가지 서로 다른 의미로 쓰이는데 한 필드에 뭉쳐 있다 — (a) TryLoad/Save 가 세우는 "메모리에 반영된 디스크 상태", (b) 감지 스레드의 CheckReload 가 세우는 "변경을 눈치챈 디스크 상태". 3-way 병합 가드(line 263)는 (a) 를 필요로 하는데 (b) 도 같은 필드를 쓴다. 감지 스레드 CheckReload 는 mtime 이 다르면 **즉시 `_lastMtime = mtime` 으로 커밋하고** WM_CONFIG_CHANGED 를 post 한다. 그러나 `_lastPersistedJson`(병합 기준선)과 `_config`(메모리)는 메인 스레드가 그 메시지를 처리할 때까지 옛 값 그대로다. 이 구간에 `Settings.Save` 가 끼어들면 `diskMtime == _lastMtime` 이 성립해 **병합을 건너뛰고**(§N-48 가드가 무장 해제) 앱의 옛 메모리 값이 사용자의 디스크 편집을 통째로 덮는다. 그 뒤 도착한 WM_CONFIG_CHANGED 는 방금 앱이 쓴 파일을 다시 읽으므로 손실은 영구화되고 로그에도 흔적이 없다("merged only the fields this save touched" Info 가 안 찍힘). 재현 근거: 두 스레드는 `_mtimeLock` 으로 직렬화되지만 순서가 보장되지 않는다 — Save 는 `JsonSerializer.Serialize`(line 188)를 락 **밖**에서 끝낸 뒤 락을 잡으므로, 그 사이 감지 스레드가 CheckReload 로 락을 먼저 얻으면 위 시퀀스가 그대로 성립한다. 창은 좁지만(락 획득 경합) 결과는 §N-48·릴리즈 리뷰 #1~#5 가 막으려던 바로 그 손실이다. 근본 수정 방향은 mtime 대신 `_lastPersistedJson` 대비


### N15 — `Program.cs` (race)

**위치**: HandleTrayToggle line 1082, HandleMenuCommand 람다 line 1165, Program.OverlayDrag.cs line 130·157 (`_config = Settings.Save(_config)`)

릴리즈 리뷰 확정 #1 은 `Settings.Save` 의 3-way 병합 결과를 `_config` 에 되돌리는 것까지만 고쳤다. **파생 캐시로는 전파되지 않는다.** `Save` 가 병합을 수행하면 반환된 config 에는 사용자가 디스크에 직접 넣은 필드가 새로 들어온다. 그런데 세 호출 지점 모두 `_config` 대입이 **마지막 문장**이고, 그 뒤에 `Overlay.HandleConfigChanged` / `CursorOverlay.HandleConfigChanged` / `ImeStatus.UpdateDetectionMethod` / `I18n.Load` / `Logger.Initialize` / `ApplyTrayEnabledTransition` 중 무엇도 재실행되지 않는다. HandleTrayToggle(1080-1094)은 아예 그중 어느 것도 호출하지 않고, HandleMenuCommand 람다(1130-1165)는 전부 **병합 전 인스턴스**로 이미 적용을 끝낸 뒤 마지막 줄에서 `_config` 만 바꾼다. 그리고 Save 는 `_lastMtime` 을 self-bump 하므로 감지 스레드의 5초 mtime 폴러가 WM_CONFIG_CHANGED 를 절대 발사하지 않는다 — 즉 **자동 자기치유 경로가 원천 차단된다**(dev-notes/2026-05-21 의 I18n self-bump 결함과 동일 뿌리, 그때는 메뉴 경로만 막았다). 재현: config.json 에서 `cursor_outer_radius` 를 손으로 바꿔 저장 → 5초 폴링 전에 트레이 아이콘 좌클릭(HandleTrayToggle). Save 가 병합해 디스크·`_config` 에는 새 반지름이 들어가지만 `CursorOverlay._config`(App/UI/CursorOverlay.cs


### N16 — `App/UI/Tray.cs` (reentrancy)

**위치**: HandleMenuCommand line 290(`config = currentConfig()`) vs IDM_SIZE_CUSTOM line 304-310, CleanupPositions line 674-677

AUDIT §B 수정은 `config = currentConfig()` 를 **함수 진입 시 1회**만 다시 읽는다. 그런데 두 메뉴 명령은 그 뒤에 **스스로 중첩 모달 루프를 연다** — `ScaleInputDialog.Show`(304) 와 `CleanupDialog.Show`(674) 는 DialogShell.Run → `ModalDialogLoop.Run` 이고, 그 루프의 `GetMessageW(out msg, IntPtr.Zero, 0, 0)`(Core/Windowing/ModalDialogLoop.cs:108)은 필터가 없어 감지 스레드가 post 한 WM_CONFIG_CHANGED 를 그대로 디스패치한다. 따라서 다이얼로그가 떠 있는 동안 `Program._config` 가 교체될 수 있는데, 확인 직후의 `updateConfig(config with { … })`(309) / `updateConfig(PositionCleanupService.RemoveSelected(config, …))`(677)는 **다이얼로그 열기 전 스냅샷**을 베이스로 쓴다. SettingsDialog 는 같은 문제를 `_currentConfigProvider?.Invoke()`(App/UI/Dialogs/SettingsDialog.cs:371)로 커밋 시점에 다시 읽어 해결했지만, 이 두 경로는 그 처방을 받지 못했다. 재현: 트레이 메뉴 → 크기 ▸ 직접 지정 을 열어 둔 채로 config.json 의 `opacity` 를 손으로 바꿔 저장 → 5초 안에 감지 스레드가 WM_CONFIG_CHANGED 를 post 하고 모달 루프가 이를 디스패치해 `_config` 가 새 opacity 로 교체됨 → 다이얼로그에서 「확인」 → `config with { IndicatorScale = … }


### N17 — `App/UI/Animation.cs` (other)

**위치**: TriggerShow line 86-90 (NonKorean+Hide 조기 반환) → Core/Animation/OverlayAnimator.cs:296 (`if (_phase == AnimPhase.Hidden) return;`) vs Program.cs:596-60

`ShowIndicatorAtForeground` 는 `_indicatorVisible = true` 를 **먼저** 세우고(Program.cs:599) `Animation.TriggerShow` 를 부른다. TriggerShow 는 `state == NonKorean && NonKoreanIme == Hide` 이면 `TriggerHide(config, forceHidden: true)` 로 빠지는데, 엔진의 `TriggerHide` 는 이미 `_phase == Hidden` 이면 **`_onHide()` 를 부르지 않고 즉시 return** 한다(OverlayAnimator.cs:296). 그러면 Program.cs:327 에 배선된 `onHidden` 훅(=`_indicatorVisible = false`)이 발화하지 않아, 배지가 실제로는 숨겨져 있는데 플래그만 true 로 남는다. 다른 모든 Hide 경로는 `Program.HideOverlay`(1016-1029)가 TriggerHide 직후 플래그를 손으로 내려 대칭을 맞추지만, TriggerShow 내부의 이 한 경로만 그 처리가 없다. `_indicatorVisible` 은 `DetectionHost.IsIndicatorVisible` 로 **감지 스레드가 읽는 유일한 가시성 계약**(Program.cs:1183)이라, 거짓 true 는 감지 루프의 레벨 트리거(DetectionService.cs:170·223·266·335·351)가 매 80ms 틱 불필요한 WM_HIDE_INDICATOR 를 쏘게 하고, `HandlePositionUpdated` 의 `wasHidden = !_indicatorVisible`(Program.cs:655) 재표시 판정을 무력화한다. 재현: `non_korean_ime = hide`(Va


### N18 — `App/Detector/DetectionService.cs` (race)

**위치**: TrackWindowMove line 386-411 (조기 return 조건 `appConfig.PositionMode != PositionMode.Window`) + DetectionState.WindowMoving

감지 스레드의 tick 간 래치 `state.WindowMoving` 은 config 교체를 전혀 인지하지 못한다. 창 이동을 감지하면 WM_HIDE_INDICATOR 를 post 하고 `WindowMoving = true` 로 래치한 뒤(399-403), 이동이 멎으면 `foregroundChanged = true` 로 되살리는(405-410) 구조인데, **되살리는 쪽이 `appConfig.PositionMode == Window` 가드 뒤에 있다**(389). 따라서 「배지 숨김」과 「배지 복구」 사이 구간에 config 리로드가 `position_mode` 를 Window → Fixed 로 바꾸면, TrackWindowMove 가 이후 매 틱 조기 return 하면서 `WindowMoving` 은 영원히 true 로 남고 **복구 경로 자체가 사라진다.** 그 틱들에서 `foregroundChanged` 는 false(같은 hwnd, 미필터)라 EmitStateChanges 도 아무것도 post 하지 않으므로, 배지는 사용자가 창을 바꾸거나 한/영을 토글할 때까지 계속 숨겨진 채로 있다. 메인 쪽 자기치유도 막힌다 — `HandleConfigChanged` 의 `RefreshVisibleIndicator()`(Program.SystemEvents.cs:34-38)는 `_indicatorVisible` 이 false 라 아무 일도 하지 않는다. 재현: position_mode=window 로 창을 드래그하는 중(배지 숨김 상태)에 config.json 의 `position_mode` 를 `fixed` 로 바꿔 저장 → 5초 폴링이 WM_CONFIG_CHANGED 를 발사 → 드래그를 멈춰도 배지가 돌아오지 않음. ProcessDetectionTick 이 틱 단위 config 스냅


### N19 — `Core/Config/JsonSettingsManager.cs` (robustness)

**위치**: Save() line 209-210 (`if (didMerge && TryLoad(out T reloaded)) return reloaded;`)

병합이 일어나면 `_lastPersistedJson = merged`(202)와 `_lastMtime`(200)은 락 안에서 확정되지만, 메모리 재동기화는 락 **밖**의 `TryLoad` 성공에만 달려 있다. `TryLoad` 가 false 를 돌려주면(방금 쓴 파일을 백신·에디터·동기화 클라이언트가 순간적으로 잠그는 IOException 등) Save 는 조용히 `value`(=병합 **전** 인메모리)를 반환한다. 그 상태의 불변식은 이렇다 — 디스크와 기준선에는 병합된 값이, 메모리에는 옛 값이 있다. 다음 저장에서 `MergeChangedOntoDisk` 의 `SameJson(baseVal, nextProp.Value)`(337) 비교가 그 필드들을 "앱이 이번에 바꿨다"로 오분류하고, 규칙상 앱이 이기므로 **사용자가 방금 살아남은 편집을 앱이 되돌려 쓴다.** self-bump 때문에 핫리로드도 돌지 않아 자기치유가 없다. 릴리즈 리뷰 #1 이 "병합 결과를 메모리에 반영"으로 닫은 구멍의 실패 분기가 그대로 열려 있는 셈이다. 최소한 TryLoad 실패 시 `_lastPersistedJson` 을 기준선으로 확정하지 말거나, 실패를 Warning 으로 노출해야 한다.


### N20 — `Core/Logging/Logger.cs` (lifetime)

**위치**: Initialize line 98-103 (`StopDrainThread(); if (!enabled) return;`) + EnqueueToFile line 218-227

`log_to_file = false` 로 설정하면 `Initialize` 가 `StopDrainThread()` 로 `_drainThread = null` 을 만든 뒤 곧바로 return 한다. 그 결과 `EnqueueToFile` 의 `if (_drainThread is null)` 분기가 **영구히 참**이 되어, 레벨 필터를 통과한 모든 로그 라인이 `_preInitBuffer`("Initialize 전 임시 버퍼"라는 이름·의도)로 흘러들어가 **아무도 비우지 않는다.** FlushPreInitBuffer 는 Initialize 성공 시에만 불린다. 결과: (a) 최대 10,000개 포맷된 문자열이 프로세스 수명 내내 상주하고, (b) 상한 도달 후에는 로그 호출마다 `while (_preInitBuffer.Count >= MaxQueueSize && TryDequeue)` 축출 루프가 돌며 `_preInitDroppedCount` 가 무한 증가한다. 감지 루프가 80ms 주기라 `log_level=Debug` + `log_to_file=false` 조합에서는 수십 초 만에 상한에 닿는다. 같은 상태는 `log_to_file=true` 라도 재현된다 — Initialize 의 writer-lock 타임아웃 조기 return(line 113-118)이나 StreamWriter 생성 실패 return(line 143)을 타면 `_drainThread` 가 null 로 남아 동일한 영구 버퍼링 상태가 된다. 파일 로깅이 꺼져 있으면 큐에 넣지 않고 버리는 분기가 필요하다.


### N21 — `App/UI/Tray.cs` (reentrancy)

**위치**: HandleMenuCommand — IDM_SIZE_CUSTOM (302-312) 와 CleanupPositions (665-678). 대조군: SettingsDialog.cs:371 은 커밋 시점에 _currentConfigProvider 로 재조회함

§B(모달 중 WM_CONFIG_CHANGED 재진입) 수정이 절반만 적용됨. HandleMenuCommand:290 이 `config = currentConfig()` 로 베이스를 새로 잡지만, 그 뒤 **중첩 모달 루프를 도는 두 경로**는 루프 이전 스냅샷 위에 그대로 커밋한다. (a) IDM_SIZE_CUSTOM: `ScaleInputDialog.Show(...)`(304, ModalDialogLoop.Run 중첩 GetMessageW) 반환 후 `updateConfig(config with { IndicatorScale = rounded })`(309). (b) IDM_CLEANUP → CleanupPositions: `CleanupDialog.Show(...)`(674) 반환 후 `updateConfig(PositionCleanupService.RemoveSelected(config, ...))`(677). 재현: 「크기 → 직접 지정...」(또는 「위치 기록 정리...」) 대화상자를 열어 둔 채 5초 이상 지나는 동안 config.json 을 외부 편집기로 저장 → 감지 스레드(DetectionService.cs:113-114, ConfigCheckIntervalPolls 주기)가 CheckConfigFileChange 로 WM_CONFIG_CHANGED 를 post → ModalDialogLoop.Run 의 중첩 루프에 필터가 없어 그대로 디스패치 → HandleConfigChanged 가 _config 를 디스크 값으로 교체. 이제 확인을 누르면 stale `config` 스냅샷이 커밋되고, 뒤이은 Settings.Save 는 _lastMtime 이 방금 리로드로 동기화돼 있어 3-way 병합도 건너뛰므로 사용자의 파일 편집이 통째로 되돌아가 디스크에 확정된다. Set


### N22 — `Core/Config/JsonSettingsManager.cs` (race)

**위치**: CheckReload() 415-423 (감지 스레드) vs Save() 186-203 / MergeOntoDiskIfChanged() 263 (메인 스레드)

_lastMtime 과 _lastPersistedJson 은 「지금 우리가 알고 있는 디스크 상태」라는 한 쌍의 불변식인데, 감지 스레드의 CheckReload 가 **_lastMtime 만 단독으로 전진**시킨다(418-419: `_lastMtime = mtime; return true;`). 그 순간 _lastPersistedJson 은 여전히 옛 내용이다. Save 의 병합 게이트는 `if (diskMtime == _lastMtime) return nextJson;`(263) 이라, 이 창 안에서 저장이 일어나면 **디스크가 실제로 바뀌었는데도 '안 바뀐 것'으로 판정되어 3-way 병합 전체가 우회**되고 사용자 편집이 전부 덮인다. 락은 걸려 있지만 락이 지키는 것은 필드 원자성뿐이고 쌍-불변식은 두 스레드에 걸쳐 깨진다. 재현: 사용자가 config.json 을 외부에서 저장 → 메인 스레드가 Save 진입해 `JsonSerializer.Serialize`(188, **락 밖**)를 수행하는 사이 감지 스레드가 _mtimeLock 을 먼저 잡아 CheckReload 실행 → _lastMtime 이 디스크 값으로 갱신 → 메인이 락에 진입해 병합 없이 WriteAllText. 결과적으로 §N-48 병합 안전망이 그 저장 1회에 대해 무력화된다(로그·경고 없음). 창을 더 넓히는 조건: PostMessageW(WM_CONFIG_CHANGED) 실패, 또는 메인 스레드가 메시지를 펌프하지 않는 구간(Logger.Initialize 의 Join 3s + 락 1s, StartupTaskManager 락 대기)에서 CheckReload 가 먼저 도는 경우.


### N23 — `Core/Logging/Logger.cs` (reentrancy)

**위치**: StopDrainThread 354-418 이 Program.cs:149-160 의 AppDomain.UnhandledException 핸들러(Logger.Shutdown, 159)에서 임의 스레드로 호출됨

크래시 핸들러는 **예외를 던진 스레드 위에서** 실행되는데(감지/LogDrain/UpdateChecker/StartupPathSync 모두 가능) Logger.Shutdown → StopDrainThread 는 그 사실을 전혀 가정하지 않는다. 두 가지 결함. (1) 자기-Join: 예외가 LogDrain 스레드에서 난 경우(코드 주석 405-414 가 ObjectDisposedException/NullReferenceException 으로 그 경로를 명시적으로 인정) `thread`(356) 가 곧 Thread.CurrentThread 이고 366 의 `thread.Join(3000)` 은 자기 자신을 기다려 3초 그대로 소모한 뒤 false 를 돌려준다. (2) 락 재진입: Monitor 는 재진입 가능하므로 370 의 `Monitor.TryEnter(_writerLock, 1000)` 이 즉시 성공하고, 393 에서 **방금 예외를 던진 바로 그 FlushQueueLocked 를 다시 실행**한다 — 크래시 핸들러 안에서 같은 예외가 재발할 수 있고, 그러면 koenvue.log 잔여 flush 도 breadcrumb 도 남지 않는다(_writerLock 이 '메인 Dispose 와 절대 안 겹친다'는 문서상 보장이 동일 스레드 크래시 경로에서는 성립하지 않음). (3) 부가: 이 핸들러의 StopDrainThread 가 메인 스레드의 Logger.Initialize(103-158)와 동시에 돌면 _generation / _drainThread 를 서로 덮어써, 새로 뜬 drain 스레드가 자기 세대 검사(245)에 걸려 한 번도 flush 하지 않고 즉시 종료한다.


### N24 — `Core/Logging/Logger.cs` (visibility)

**위치**: _drainThread 필드 선언 22 — 쓰기: Initialize 153 / StopDrainThread 357 (메인 스레드), 읽기: EnqueueToFile 220 (감지·UpdateChecker·StartupPathSync·drain 등 모든 스레드)

_drainThread 는 non-volatile 인데 같은 파일의 다른 cross-thread 필드(_generation)는 Volatile.Read/Interlocked 로 다루고 있어 명백한 누락이다. 두 가지 결과. (1) 가시성: 감지 스레드가 EnqueueToFile 진입 시 stale 값을 읽어, 이미 null 이 된 뒤에도 _logQueue 로 넣거나(그 메시지는 drain 되지 않음) 이미 새 스레드가 뜬 뒤에도 _preInitBuffer 로 우회한다. FlushPreInitBuffer(162)가 이미 지나간 뒤 버퍼에 들어간 줄은 **다음 Logger.Initialize 까지 영구 미기록**이다. (2) 더 실질적인 파생: 런타임에 log_to_file 을 끄면(HandleConfigChanged → Logger.Initialize(false, ...) → StopDrainThread 로 _drainThread=null 후 103 에서 early return) _drainThread 가 영영 null 로 남아, 이후 **모든 스레드의 모든 로그가 _preInitBuffer 에 누적**된다. 드레인 주체가 없으므로 MaxQueueSize(10,000)까지 문자열을 붙들고 있다가 최고령부터 조용히 버린다 — '파일 로깅 OFF' 가 '드롭'이 아니라 '무한 보류'로 동작한다. 재현: log_to_file=true 로 기동 → config.json 에서 log_to_file=false 로 저장 → 감지 스레드의 CheckConfigFileChange 가 리로드를 유발 → 이후 Logger.Debug/Warning 이 큐에만 쌓이는지 _preInitBuffer.Count 로 확인.


### N25 — `Program.cs` (robustness)

**위치**: ShowIndicatorAtForeground 596-602 (`_indicatorVisible = true` 무조건) → App/UI/Animation.cs:86-90 (NonKorean+Hide 시 TriggerShow 가 TriggerHide 로 전환) → Cor

감지 스레드가 레벨 트리거 게이트로 읽는 공유 플래그 _indicatorVisible(host.IsIndicatorVisible)이 **화면 상태와 어긋난 채 true 로 남는 세 번째 경로**가 남아 있다. §N-34 는 애니메이터 자체 숨김을, 확정 #13 은 드래그 종료 숨김을 각각 onHidden/EndDrag 로 막았는데, NonKorean+Hide 경로는 어느 쪽도 안 탄다: ShowIndicatorAtForeground 가 먼저 `_indicatorVisible = true` 를 박고(599) Animation.TriggerShow 가 NonKoreanImeMode.Hide 가드로 TriggerHide(forceHidden:true) 를 부르는데, 애니메이터가 이미 Hidden phase 면 OverlayAnimator.TriggerHide 가 296 에서 **즉시 return** 하여 _onHide()/onHidden 훅이 발화하지 않는다. 재현: 일본어·중국어 IME 환경(기본값 non_korean_ime=hide)에서 앱 기동 → 첫 WM_FOCUS_CHANGED/WM_IME_STATE_CHANGED 로 ImeState.NonKorean 도달 → 배지는 안 보이는데 _indicatorVisible=true. 이후 바탕화면/작업표시줄로 포커스를 옮길 때마다 감지 스레드의 TryHandleFilter(266)·TryHandleModalGate(171)·TrackWindowMove(399) 가 게이트를 통과해 불필요한 WM_HIDE_INDICATOR 왕복 + `Filter triggered HIDE`/`HideOverlay called` Info 로그 2줄을 매 전환마다 남기고, HandleCapsLockTimer(Program.Timers.cs:111)는 숨겨진 창에 대


### N26 — `App/UI/Tray.cs` (reentrancy)

**위치**: Tray.HandleMenuCommand, case IDM_SIZE_CUSTOM (lines 302–312)

§B stale-commit-base bug survives in the modal menu paths. HandleMenuCommand refreshes its base once at entry (`config = currentConfig();`, line 290), then `ScaleInputDialog.Show(_hwndMain, config.IndicatorScale)` enters ModalDialogLoop.Run — an UNFILTERED `GetMessageW(out msg, IntPtr.Zero, 0, 0)` pump (ModalDialogLoop.cs:108). The detection thread keeps running `Settings.CheckConfigFileChange` every ~5 s (DetectionService.ProcessDetectionTick line 113 — it executes BEFORE the modal gate at line 132, so the gate does not suppress it), so WM_CONFIG_CHANGED is posted and dispatched inside the dialog's nested loop → Program.HandleConfigChanged replaces `_config` with the externally edited file. On 확인, line 309 commits `config with { IndicatorScale = rounded }` where `config` is the PRE-modal snapshot, and Program's updateConfig lambda does `_config = ThemePresets.Apply(newConfig)` + `Settin


### N27 — `App/UI/Tray.cs` (reentrancy)

**위치**: Tray.CleanupPositions (lines 665–678), reached from HandleMenuCommand case IDM_CLEANUP

Same root cause as the IDM_SIZE_CUSTOM defect, worse exposure because the cleanup dialog can sit open indefinitely. Line 674 `CleanupDialog.Show(_hwndMain, displayItems)` enters ModalDialogLoop.Run and pumps; every WM_CONFIG_CHANGED that arrives is dispatched and swaps `_config`. Line 677 then commits `PositionCleanupService.RemoveSelected(config, …)` against the pre-dialog `config` snapshot, and updateConfig persists it. Every field the user edited in config.json while the dialog was open is silently reverted on disk. Additionally, `displayItems`/`originalNames` were computed from the stale snapshot (line 667), so if the reload changed `indicator_positions`, RemoveSelected maps checkbox selections onto entries that no longer correspond to the live dictionary — it can delete the wrong process's saved position or miss the selected one.


### N28 — `Program.cs` (visibility)

**위치**: Program.HandleMenuCommand — updateConfig lambda (lines 1126–1166), `_config = Settings.Save(_config);` at line 1165

In this lambda `Settings.Save` is called LAST, after every side effect. Save can return a DIFFERENT instance than it was given (3-way merge, JsonSettingsManager.Save lines 209–210 re-loads from disk), and its mtime self-bump means the reconciling WM_CONFIG_CHANGED will never fire. So when a merge happens, `_config` ends up holding disk-side values that were never applied to anything: Overlay.HandleConfigChanged (1144), ApplyCursorConfigChange (1146), ApplyUserHiddenTransition (1151), ShowIndicatorAtForeground/UpdateColor (1157/1160) and ApplyTrayEnabledTransition (1163) all already ran against the pre-merge value and are never re-run. Concrete failure: user edits `cursor_indicator_enabled: false` in config.json; before the 5 s poller sees it, user picks 투명도 from the tray menu. Save merges → `_config.CursorIndicatorEnabled == false`, but ApplyCursorConfigChange already ran with true, so `


### N29 — `Core/Windowing/ModalDialogLoop.cs` (other)

**위치**: ModalDialogLoop.Run finally block, line 129 `User32.SetForegroundWindow(hwndOwner);`

Every modal dialog restores foreground to `hwndOwner`, which is always `_hwndMain` — a 0×0 window created with dwStyle 0, i.e. never WS_VISIBLE (Program.Bootstrap.CreateMainWindow lines 148–151). SetForegroundWindow succeeds on such a window (it is the same Q135788 trick Tray.ShowMenu line 234 relies on), so after closing 상세 설정 / 위치 기록 정리 / 배율 직접 지정 the foreground is KoEnVue's invisible message window. DetectionService.ProcessDetectionTick then hits the self-HWND guard (`hwndForeground == host.GetHwndMain() → return`, lines 124–127) on EVERY tick and posts nothing. Since TryHandleModalGate already posted WM_HIDE_INDICATOR while the dialog was up (line 176) and left `state.LastFiltered = true`, the badge stays gone and no IME/focus/position update is emitted until the user manually clicks some other window. On Cancel nothing at all runs afterwards (SettingsDialog.Show only invokes `_updat


### N30 — `Core/Logging/Logger.cs` (lifetime)

**위치**: Logger.cs:403-417 (StopDrainThread 의 lock-busy else 분기) → Logger.cs:113-149 (Initialize 의 무조건 _fileWriter 대입, 특히 133행)

StopDrainThread 는 _writerLock 을 WriterLockTimeoutMs(1000ms) 안에 못 잡으면 「writer 를 아예 건드리지 않는다」는 의도적 분기로 물러난다(405-417) — Dispose 도 null 대입도 하지 않는다. 그런데 Initialize 는 그 직후 락을 다시 시도해 성공하면 133행에서 `_fileWriter = new StreamWriter(...)` 로 옛 참조를 무조건 덮어쓴다. 옛 StreamWriter/FileStream/SafeFileHandle 은 아무도 Dispose 하지 않아 파이널라이저까지 고아로 남고, 그 핸들이 koenvue.log 를 FileShare.Read 로 계속 점유하므로 새 StreamWriter(append:true) 는 IOException 으로 실패 → catch(136-144)가 _fileWriter=null 로 떨어뜨려 **파일 로깅이 그 세션 내내 무음**이 된다(TryReopenWriter 도 같은 이유로 계속 실패). 재현: 로그가 log_max_size_mb 에 도달해 drain 스레드가 FlushQueueLocked 의 회전 구간(304-312: old.Dispose → File.Delete → File.Move → new StreamWriter)에서 _writerLock 을 쥔 채 AV 스캔/느린 디스크로 1초 이상 머무는 동안, config.json 의 log_to_file / log_file_path / log_max_size_mb 중 하나를 편집 → 5초 폴러가 WM_CONFIG_CHANGED → HandleConfigChanged(Program.cs:908-916)가 Logger.Initialize 호출. 같은 분기가 Shutdown() 경로(Logger.cs:182-185)에도


### N31 — `Core/Config/JsonSettingsManager.cs` (race)

**위치**: Save() line 199-210 (`_lastMtime = GetLastWriteTimeUtc` → `if (didMerge && TryLoad(out reloaded)) return reloaded;`) + 호출자 Program.cs:1082, Program.cs

Save() 의 3-way 병합 경로가 **네 번째 config 리로드 진입점**인데, HandleConfigChanged 가 하는 전이 적용을 하나도 거치지 않으면서 동시에 그 전이를 유발할 WM_CONFIG_CHANGED 까지 삼킨다. 흐름: 디스크 mtime != _lastMtime → MergeOntoDiskIfChanged 가 didMerge=true → WriteAllText 후 `_lastMtime = 방금 쓴 파일의 mtime` (line 200) → didMerge 이므로 TryLoad 재실행 (line 209) 이 다시 `_lastMtime` 을 새 파일 mtime 으로 세팅 (line 132). 즉 **감지 스레드의 CheckReload 는 이후 영원히 false** — 사용자의 외부 편집분이 `_config` 에 들어왔는데도 WM_CONFIG_CHANGED 가 발화하지 않는다 (Program.Timers.cs:66-70 에 적힌 mtime self-bump 차단이 여기서는 의도치 않게 작동). 결과: 반환된 인스턴스는 `_config` 에만 대입되고 (Program.cs:1082/1165, Program.OverlayDrag.cs:130/157) `Logger.SetLevel`·`Logger.Initialize`·`I18n.Load`·`ImeStatus.UpdateDetectionMethod`·`Overlay.HandleConfigChanged`·`ApplyCursorConfigChange`·`ApplyTrayEnabledTransition`·`Settings.ClearProfileCache` 중 어느 것도 실행되지 않는다. `Overlay._config`(App/UI/Overlay.cs:36) 와 `CursorOverlay._config`(App/UI/Curso


### N32 — `App/UI/Tray.cs` (race)

**위치**: HandleMenuCommand IDM_SIZE_CUSTOM (line 302-312) 와 IDM_CLEANUP → CleanupPositions (line 419, 665-678)

AUDIT §B(모달 중 외부 편집 되돌림) 수정이 **두 경로에만** 적용돼 있고, 중첩 모달을 도는 나머지 두 경로는 열려 있다. 적용된 곳: HandleMenuCommand 진입부 `config = currentConfig()` (Tray.cs:290) 와 SettingsDialog.TryCommit 의 `_currentConfigProvider?.Invoke()` (App/UI/Dialogs/SettingsDialog.cs:371). 누락된 곳: - `IDM_SIZE_CUSTOM`: `ScaleInputDialog.Show(...)` (Tray.cs:304) 가 반환된 **뒤** `updateConfig(config with { IndicatorScale = rounded })` (Tray.cs:309) 로 다이얼로그 열기 전 스냅샷 위에 얹는다. - `IDM_CLEANUP`: `CleanupDialog.Show(...)` (Tray.cs:674) 반환 뒤 `PositionCleanupService.RemoveSelected(config, ...)` (Tray.cs:677) — RemoveSelected 는 `config with { IndicatorPositions, IndicatorPositionsRelative }` (App/Config/PositionCleanupService.cs:91) 라 나머지 전 필드가 스냅샷 값 그대로다. 창이 열리는 근거(코드로 확인): 두 다이얼로그 모두 DialogShell.Run → ModalDialogLoop.Run (Core/Windowing/DialogShell.cs:164) 이고, 그 루프의 `GetMessageW(out msg, IntPtr.Zero, 0, 0)` 는 필터가 없어 아무 창의 post 메시지도 디스패치한다 (Core


### N33 — `Program.SystemEvents.cs` (visibility)

**위치**: HandleSettingChange (line 48-74) — WM_SETTINGCHANGE / WM_THEMECHANGED / WM_DWMCOLORIZATIONCOLORCHANGED 핸들러

OS 콜백 경로에서 `_config` 가 새 시스템 색으로 교체되는데, 커서 헤일로만 그 갱신을 전달받지 못해 자기 스냅샷에 옛 강조색을 영구 박제한다. HandleSettingChange 는 `_config = ThemePresets.Apply(_config)` (line 58) 로 HangulBg/EnglishBg 를 새 accent 로 재계산한 뒤 `Overlay.HandleConfigChanged(_config)` (line 63), `RefreshVisibleIndicator()` (line 67), `Tray.UpdateState(..., _config)` (line 73) 까지 갱신한다. 그러나 `ApplyCursorConfigChange()` / `CursorOverlay.HandleConfigChanged(_config)` 는 호출하지 않는다. `CursorOverlay._config` (App/UI/CursorOverlay.cs:41) 는 값 복사 스냅샷이고, 갱신 지점은 `Initialize` (Program.Timers.cs:29) 와 `HandleConfigChanged` (Program.Timers.cs:85) 단 두 곳뿐이다 — grep 으로 전 호출처 확인함. 그리고 헤일로 색은 `BuildStyle(config, state, capsOn)` 이 `config.HangulBg/EnglishBg/NonKoreanBg` 에서 뽑는다(CursorOverlay.cs:446-471, App/UI/Dialogs/SettingsDialog.Fields.cs:148 주석이 "배경색은 CursorOverlay.BuildStyle 이 커서 동심원 색으로도 그대로 사용" 이라고 명시). `SetImeState`/`SetCapsLock` 의 재합성도 `RebuildStylePr


### N34 — `Program.cs` (reentrancy)

**위치**: HandleConfigChanged 파싱 실패 분기 (line 891-901) → Tray.ShowConfigReloadFailed → Tray.ShowMessage (App/UI/Tray.cs:604-606, 616-617)

config 리로드 실패 안내가 메시지를 펌프하는 MessageBoxW 라, 그 안에서 감지 스레드의 다음 WM_CONFIG_CHANGED 가 HandleConfigChanged 를 **재진입**시킨다. 재진입한 리로드가 성공하면 `_configReloadFailed` 래치가 풀려 "연속 실패당 1회" 설계(AUDIT §G, line 894 주석)가 깨지고 안내 박스가 무한히 쌓인다. 경로: 실패 분기가 `_configReloadFailed = true` 후 `Tray.ShowConfigReloadFailed()` (Program.cs:898) → `ModalDialogLoop.RunExternal(_hwndMain, () => User32.MessageBoxW(...))` (Tray.cs:605-606). MessageBoxW 는 Win32 자체 모달 루프라 `_hwndMain` 앞으로 post 된 메시지를 그대로 디스패치한다. 감지 스레드는 모달 여부와 무관하게 `Settings.CheckConfigFileChange` 를 계속 돌리므로(DetectionService.cs:113-114 — 모달 게이트 line 132 보다 앞) 사용자가 편집기에서 파일을 저장할 때마다 WM_CONFIG_CHANGED 가 발화한다. 재현: (1) config.json 을 문법 오류 상태로 저장 → 5초 뒤 안내 박스가 뜬다(박스를 닫지 않고 그대로 둔다). (2) 편집기에서 오류를 고쳐 저장 → 박스의 펌프 안에서 HandleConfigChanged 가 재진입해 성공 경로를 전부 실행한다(`_config` 교체, `Logger.Initialize` 가 drain 스레드 Join 최대 3초 + writer lock 1초 동안 그 중첩 펌프를 블록, `ApplyCursorConfigChange` 가 헤


### N35 — `Core/Logging/Logger.cs` (race)

**위치**: Initialize (98-163) + StopDrainThread (354-418) — _drainThread field at line 22

Logger.Initialize / StopDrainThread mutate the non-volatile `_drainThread` and `_generation` OUTSIDE `_writerLock`, and they are reachable from more than one thread. Main thread: HandleConfigChanged -> Logger.Initialize (Program.cs:913). Background threads: RegisterCrashHandlers' AppDomain.UnhandledException handler calls `Logger.Shutdown()` -> StopDrainThread (Program.cs:159) and that handler runs on the THROWING thread — DetectionService.RunLoop, UpdateChecker, or StartupPathSync. Interleaving: main is inside Initialize between `_drainThread = new Thread(DrainLoop)` (153) and `_drainThread.Start(generation)`/`FlushPreInitBuffer()` (158-162); detection thread throws, its crash handler runs StopDrainThread, reads `thread = _drainThread` (the not-yet-started new one), writes `_drainThread = null` (357) and `Interlocked.Increment(ref _generation)` (360). Main then Starts a thread whose cap


### N36 — `Core/Logging/Logger.cs` (robustness)

**위치**: Initialize (113-118 Monitor.TryEnter early return; 136-144 StreamWriter failure early return)

Two early-return paths in Initialize leave `_drainThread == null` permanently, killing all file logging process-wide. Sequence: StopDrainThread() at line 101 has already nulled `_drainThread`; if `Monitor.TryEnter(_writerLock, 1000)` times out (a zombie drain thread stuck in File.Move/File.Delete during rotation — AV scan, network path, tail viewer) the method returns at 117, and if `new StreamWriter(...)` throws IOException/UnauthorizedAccessException it returns at 143. In both cases no drain thread is ever created again. From then on EnqueueToFile (220) routes every message from every thread into `_preInitBuffer`, which is only drained by FlushPreInitBuffer inside a *later successful* Initialize — and Initialize only runs when log_to_file / log_file_path / log_max_size_mb change (Program.cs:908-911), i.e. usually never again. The `TryReopenWriter` self-heal (330-343) can't help because


### N37 — `Program.cs` (robustness)

**위치**: ShowIndicatorAtForeground (596-602) -> App/UI/Animation.cs TriggerShow (86-90) -> Core/Animation/OverlayAnimator.cs TriggerHide (296)

`_indicatorVisible` — the flag the detection thread reads through DetectionHost.IsIndicatorVisible on every poll — is latched true with no path back to false when the NonKorean guard fires against an already-hidden animator. ShowIndicatorAtForeground sets `_indicatorVisible = true` (599) BEFORE calling Animation.TriggerShow; TriggerShow immediately detects `state == ImeState.NonKorean && config.NonKoreanIme == NonKoreanImeMode.Hide` (Animation.cs:86, and Hide is the default per Settings.Validate:204) and delegates to TriggerHide(forceHidden:true); OverlayAnimator.TriggerHide returns at its first line when `_phase == AnimPhase.Hidden` (296), so `_onHide()` never runs, so the §N-34 `onHidden` hook wired at Program.cs:327 never clears the flag. `_phase` starts at AnimPhase.Hidden (OverlayAnimator.cs:35), so the very first NonKorean IME notification after boot leaves the flag lying. Repro: J


### N38 — `App/UI/Tray.cs` (race)

**위치**: HandleMenuCommand IDM_SIZE_CUSTOM (302-312) + CleanupPositions (665-678, 호출 419) — 대비: 290행 `config = currentConfig();`

§B 의 lost-update 가 **중첩 다이얼로그를 여는 두 메뉴 명령에서만 그대로 열려 있다.** §B 수정은 HandleMenuCommand 진입부에서 `config = currentConfig()` 로 베이스를 새로 잡았지만, IDM_SIZE_CUSTOM 과 IDM_CLEANUP 은 그 뒤에 **자체 모달 루프를 가진 다이얼로그**(ScaleInputDialog / CleanupDialog)를 열고, 다이얼로그가 닫힌 뒤 **열기 전에 잡은 `config` 로** `config with { IndicatorScale = rounded }` / `PositionCleanupService.RemoveSelected(config, …)` 를 합성한다. 다이얼로그는 몇 분이고 열려 있을 수 있고 그 동안 `ModalDialogLoop.Run` 의 중첩 GetMessageW 가 감지 스레드의 `WM_CONFIG_CHANGED`(5초 폴링)를 그대로 디스패치해 `Program._config` 를 새 인스턴스로 교체한다. SettingsDialog 는 `_currentConfigProvider` 로 커밋 시점 값을 다시 읽어 이 문제가 없다 — 두 경로만 누락. 재현: 트레이 → 「크기 배율 → 직접 지정」(또는 「위치 기록 정리」) 으로 다이얼로그를 연 채 5초 이상 두고, 그 사이 `config.json` 의 `hangul_bg` 를 외부 편집기로 바꾼다(핫리로드가 적용돼 배지 색이 즉시 바뀌는 것으로 확인 가능) → 다이얼로그에서 「확인」/「삭제」 → 색이 옛 값으로 되돌아가고 **디스크에도 옛 값이 확정된다.** 디스크 확정까지 가는 이유: 핫리로드의 `TryLoad` 가 JsonSettingsManager.cs:132-135 에서 `_lastMtime` 과 3-way 병합 기준선


### N39 — `Core/Windowing/ModalDialogLoop.cs` (reentrancy)

**위치**: Run() 99행 `User32.EnableWindow(hwndOwner, false)` — 연쇄: Program.Bootstrap.cs:162-177(CreateOverlayWindow, hWndParent=IntPtr.Zero) → Program.cs:524-579

모달 가드가 **메인 윈도우 하나만** 비활성화하는데, 플로팅 배지 `_hwndOverlay` 는 소유자 없는 별도 최상위 창(WS_EX_TOPMOST)이라 EnableWindow 대상이 아니다. 따라서 다이얼로그가 떠 있는 동안에도 배지는 클릭·드래그가 되고, 드래그는 `WM_NCLBUTTONDOWN/HTCAPTION` 승격으로 **DefWindowProc 의 sizemove 모달 루프를 다이얼로그 모달 루프 안에 3중으로 중첩**시킨다. 드래그 종료 `WM_EXITSIZEMOVE` → `HandleOverlayDragEnd` 가 `_config` 를 `with` 로 교체하고 `Settings.Save` 로 **디스크 I/O 까지 수행**한다(OverlayDrag.cs:128-130 / 147 / 155-157) — 즉 열려 있는 다이얼로그 뒤에서 설정이 갈아치워진다. 위 결함 1과 합쳐지면 방금 저장한 위치가 「삭제」/「확인」 한 번에 되돌아간다. 배지가 살아 있는 이유(전제 확인): `DetectionService.TryHandleModalGate`(DetectionService.cs:163-181)는 `ModalDialogLoop.IsActive` **그리고** 포그라운드가 자기 프로세스일 때만 HIDE 를 보낸다 — Alt+Tab 으로 외부 앱에 포커스를 넘기면(의도된 동작) 가드가 빠지고 배지가 다시 표시된다. 재현: 트레이 → 「위치 기록 정리」 → Alt+Tab 으로 메모장 전환(배지 재표시 확인) → 배지를 드래그해 다른 곳에 놓음(`config.json` 의 `indicator_positions` 갱신 확인) → Alt+Tab 으로 다이얼로그 복귀 → 「삭제」 → 방금 저장한 좌표가 사라진다. 부가 위험: sizemove 루프 안에서 `HandleConfigChang


### N40 — `Program.cs` (lifetime)

**위치**: WndProcCore WM_DESTROY 분기 509-512행 — 대비: Program.Bootstrap.cs:264-283 (§N-42 수정 위치)

AUDIT-2026-07-30 §N-42 는 '메인 윈도우가 정상 루프 종료 전 파괴되는 경로에서 핸들 필드 미리셋'을 닫았다고 기록돼 있으나, 실제 수정은 `OnProcessExit` 안에만 들어갔다(Bootstrap.cs:269-283). **WM_CLOSE 경로는 그대로 열려 있다.** WndProcCore 에 `WM_CLOSE` 분기가 없으므로 `default:` → `DefWindowProcW` 가 `DestroyWindow(_hwndMain)` 을 수행하고, 뒤이어 도착한 `WM_DESTROY` 는 `PostQuitMessage(0)` 만 하고 **`_hwndMain` 을 Zero 로 내리지 않는다.** 그 결과 메시지 큐가 WM_QUIT 까지 배수되고 MainImpl 이 반환해 OnProcessExit 가 도는 구간(감지 루프 sleep 최대 `MaxPollMs + DetectionBackoffMaxMs`) 내내 세 volatile 핸들 필드가 죽은 값을 들고 있다. 감지 스레드는 `host.GetHwndMain()` 가드(`!= IntPtr.Zero`)를 통과해 `WM_POSITION_UPDATED`/`WM_IME_STATE_CHANGED`/`WM_HIDE_INDICATOR`(= WM_APP+1..4) 를 계속 `PostMessageW` 한다 — §N-42 가 막으려던 바로 그 상태이며, 커널이 그 HWND 값을 재발급하면 무관한 창에 WM_APP 범위 메시지가 배달된다(§L 과 같은 재활용 문제). 재현: 트레이 메뉴 → 「관리자 권한으로 실행」 토글. `Tray.HandleMenuCommand` IDM_ADMIN_ELEVATION(Tray.cs:351)이 `PostMessageW(hwndMain, WM_CLOSE)` 를 보내 이 경로를 **정상 동작으로** 탄다. O


### N41 — `App/UI/Tray.cs` (reentrancy)

**위치**: ShowMessage 604-606행 `ModalDialogLoop.RunExternal(_hwndMain, …)` — 계약 위반 대상: Core/Windowing/ModalDialogLoop.cs:36-40(ExternalModalSentinel), 75-84(Rej

`ExternalModalSentinel` 은 '외부 모달(MessageBoxW)은 진짜 창 핸들이 없으니 `RejectReentry` 의 `SetForegroundWindow` 대상에서 제외한다'는 계약을 위해 존재하고, `RejectReentry` 도 `active != ExternalModalSentinel` 일 때만 SetForegroundWindow 를 부른다. 그런데 `RunExternal` 은 `hwndSentinel != IntPtr.Zero` 이면 그 값을 **그대로** 저장하므로(148-150행), `Tray.ShowMessage` 가 `_hwndMain` 을 넘기는 순간 센티넬 경로가 통째로 우회된다 — MessageBoxW 구간인데도 `s_activeDialog == _hwndMain` 이 되어 배제 조건이 성립하지 않는다. 결과: 트레이 MessageBox(관리자 권한 안내 / 「위치 기록이 없습니다」 / config 리로드 실패 안내)가 떠 있는 동안 사용자가 트레이 아이콘을 조작하면, MessageBoxW 의 자체 메시지 루프가 `WM_TRAY_CALLBACK` 을 디스패치 → `HandleTrayCallback` → `Tray.ShowMenu`(Tray.Menu.cs:29) 또는 좌클릭 가드(Program.cs:1044) → `RejectReentry()` → **`SetForegroundWindow(_hwndMain)`**. `_hwndMain` 은 `CreateWindowExW(0, MainClassName, …, 0, 0,0,0,0)` 로 만든 스타일 0(비가시) 0×0 메시지 전용 창이다(Program.Bootstrap.cs:146-151). 즉 포커스 복원 대상이 '사용자에게 보이지 않는 창'이 되어, 의도(기존 모달로 포커스 되돌리기)와 정반대로


### N42 — `App/UI/Tray.cs` (reentrancy)

**위치**: UpdateState 214-218행 (`_notifyIcon?.UpdateIconAndTooltip(newIcon…)` → `_currentIcon?.Dispose()` → `_currentIcon = newIcon`)

`Shell_NotifyIconW` 는 explorer 로 가는 **프로세스 간 SendMessage** 라, 호출 스레드가 블록되는 동안 Windows 는 이 스레드로 들어온 *sent* 메시지를 계속 디스패치한다. `Tray.UpdateState` 는 그 블로킹 호출을 사이에 두고 `_currentIcon` 을 **읽고(214행 이전에 이미 shell 로 넘긴 새 핸들) → 해제(217행) → 재대입(218행)** 하므로, 그 창에 같은 함수가 재진입하면 세 필드가 뒤엉킨다. 재진입 경로: `WM_SETTINGCHANGE` / `WM_THEMECHANGED` 는 시스템·다른 프로세스가 `SendMessageTimeout(HWND_BROADCAST, …)` 로 **보내는** 메시지라 위 대기 중에 배달된다 → `Program.WndProcCore` 491-495행 → `HandleSettingChange` → `Tray.UpdateState`(Program.SystemEvents.cs:72-73) 재진입. 결과(순서 추적): 바깥 호출이 iconA 를 shell 에 등록하고 블록 → 안쪽 호출이 iconB 를 등록하고 `_currentIcon`(원본) 을 해제한 뒤 `_currentIcon = iconB` → 바깥이 재개해 `_currentIcon?.Dispose()` 로 **shell 이 지금 표시 중인 iconB 의 HICON 을 파괴**하고 `_currentIcon = iconA` 로 되돌린다. 트레이에 빈칸/깨진 아이콘이 남고, `_currentIcon` 은 shell 이 참조하지 않는 핸들을 가리킨 채 다음 갱신 때 또 어긋난다. 같은 패턴이 `HandleAddRetryTimer`(154-177행)에도 있다 — WM_TIMER 핸들러 안에서 `_notifyIcon.Add` 


### N43 — `Program.cs` (lifetime)

**위치**: WndProcCore, WM_DESTROY case (Program.cs:509-512) / 트리거: App/UI/Tray.cs:351 (IDM_ADMIN_ELEVATION)

창 자기파괴(WM_CLOSE) 경로가 AUDIT §N-42 가 세운 "파괴 직후 핸들 필드를 즉시 Zero" invariant를 통째로 우회한다. WndProcCore 의 switch 에는 WM_CLOSE case 가 없어(Program.cs:427-583, default → DefWindowProcW:582) DefWindowProcW 가 곧바로 DestroyWindow(_hwndMain) 를 수행하는데, 뒤따르는 WM_DESTROY 핸들러는 `if (hwnd == _hwndMain) PostQuitMessage(0)` 만 하고 _hwndMain/_hwndOverlay/_hwndCursorOverlay 를 비우지 않는다. 즉 §N-42 의 필드 리셋은 OnProcessExit(Program.Bootstrap.cs:269-283) 한 곳에만 있고, 이 경로에서는 그보다 먼저 창이 죽는다. 재현 근거: 트레이 우클릭 → 「관리자 권한 실행」 토글 → Tray.cs:348 ShowMessage 확인 → Tray.cs:351 PostMessageW(hwndMain, WM_CLOSE). 이 시점부터 OnProcessExit 가 _stopping=true 를 세우고 Join(500) 을 마칠 때까지, 감지 스레드는 이미 while 가드를 지난 상태라 최소 한 틱(PollIntervalMs~최대 MaxPollMs+DetectionBackoffMaxMs)을 더 돌며 죽은 핸들에 계속 쓴다 — DetectionService.cs:176/229/272/337/353/400/422/432/440 의 PostMessageW(host.GetHwndMain(), …) 와 DetectionService.cs:114 → Settings.CheckConfigFileChange → Settings.cs:282 Po


### N44 — `App/UI/Tray.cs` (robustness)

**위치**: Tray.Initialize (Tray.cs:127-143) + Tray.HandleAddRetryTimer (Tray.cs:154-177)

트레이 최초 등록 경로만 "무효 HICON 방어" 정책에서 빠져 있고, 재시도 타이머는 그 무효 핸들을 그대로 재사용한다. TrayIcon.CreateIcon 은 실패 시 `new SafeIconHandle(IntPtr.Zero, ownsHandle:false)` 를 돌려주는데(TrayIcon.cs:104-108, 135-139), Tray.UpdateState 는 이를 `newIcon.IsInvalid` 로 걸러 이전 아이콘을 유지하는 우아한 열화가 있는 반면(Tray.cs:200-212), Initialize 는 검사 없이 `_notifyIcon.Add(_currentIcon.DangerousGetHandle(), …)`(Tray.cs:130) 로 NULL HICON 을 NIF_ICON 과 함께 셸에 등록하고 무효 핸들을 _currentIcon 에 그대로 보관한다. 이어 HandleAddRetryTimer 는 `_currentIcon is null` 만 확인하고(Tray.cs:156) 최대 30회 동안 같은 무효 핸들로만 Add 를 반복한다(Tray.cs:164) — 아이콘을 다시 만들지 않는다. 재현 근거: 부팅 자동 시작(schtasks LogonTrigger, StartupTaskManager.cs:49)처럼 explorer 초기화 전에 기동돼 NIM_ADD 가 실패하는 구간은 GDI/USER 자원 압박 구간과 겹치므로 CreateIcon 실패(GetSystemMetrics 기반 DIB 생성)와 동시 발생이 성립한다. 그 상태에서 Add 가 성공하면 빈 칸 아이콘이 박히고, 실패하면 30회 재시도가 전부 무의미해진 뒤 _initialized=true·_added=false 로 남아 이후 UpdateState 의 NIM_MODIFY 가 전부 early-return(NotifyI


### N45 — `Program.Bootstrap.cs` (other)

**위치**: OnProcessExit 단계 5 (Program.Bootstrap.cs:264-283) vs 단계 7 주석 (Program.Bootstrap.cs:290-291)

종료 시퀀스 안에서 서로를 무효화하는 두 전제가 공존한다. 단계 7 주석은 "ProcessExit 는 finalizer 스레드에서 돌아 메인 스레드의 apartment 와 매칭되지도 않는다" 라고 단언하는데, 이 전제가 참이면 바로 위 단계 5 의 DestroyWindow(_hwndOverlay/_hwndCursorOverlay/_hwndMain) 3회는 전부 조용히 실패한다 — Win32 는 다른 스레드가 만든 창의 DestroyWindow 를 거부한다(반환 false, 창은 살아남음). 그러면 §N-42 가 도입한 "파괴 직후 필드를 Zero" 라인은 실제로 파괴되지 않은 창의 핸들을 지우는 셈이 되고, 단계 2·2a 의 KillTimer(_hwndMain, …) 도 같은 스레드 친화성 가정 위에 있다. 반대로 실제로 메인 스레드에서 실행된다면(정상 Main 반환 종료의 CoreCLR 동작) 단계 7 주석이 사실과 다르며, COM 해제를 생략한 근거 자체가 틀린 전제 위에 서게 된다. 어느 쪽이든 한쪽은 결함이다: 검증 방법은 OnProcessExit 진입 시 Environment.CurrentManagedThreadId 와 DestroyWindow 반환값·GetLastError 를 Debug 로 남겨 실측하는 것이며, 이는 AUDIT §I 가 "사실과 다른 주석" 을 정정한 것과 같은 유형의 미정리 잔재다.


### N46 — `App/UI/Tray.cs` (reentrancy)

**위치**: HandleMenuCommand — IDM_SIZE_CUSTOM (302-312, 특히 309행) / CleanupPositions (665-678, 특히 677행)

HandleMenuCommand 첫 문장의 `config = currentConfig()` (290행) 은 AUDIT §B(TrackPopupMenu 중 _config 교체) 를 막지만, **중첩 모달 루프를 여는 두 경로는 그 뒤로 스냅샷을 갱신하지 않는다**. ScaleInputDialog.Show / CleanupDialog.Show 는 DialogShell.Run → ModalDialogLoop.Run(Core/Windowing/DialogShell.cs:164) 의 필터 없는 `GetMessageW(…, IntPtr.Zero, 0, 0)` 로 몇 분이고 메시지를 디스패치한다. 그 사이 (a) 감지 스레드가 5초 폴링으로 post 한 WM_CONFIG_CHANGED → Program.HandleConfigChanged 가 `_config = loaded` 로 교체, (b) `EnableWindow(hwndMain,false)` 는 _hwndOverlay(별도 top-level) 를 막지 않으므로 사용자가 배지를 드래그 → HandleOverlayDragEnd 가 `_config = _config with { IndicatorPositions… }` + Settings.Save 수행. 다이얼로그 반환 후 309/677행은 **다이얼로그를 열기 전의 `config`** 를 베이스로 `with` 를 얹어 updateConfig 에 넘기고, 그 람다 끝의 Settings.Save 가 디스크까지 덮는다. 3-way 병합도 못 막는다 — (a) 경로는 HandleConfigChanged 의 TryLoad 가, (b) 경로는 드래그의 Save 가 이미 `_lastMtime`/`_lastPersistedJson` 을 동기화해 MergeOntoDiskIfChanged 가 `diskMtime =


### N47 — `Program.cs` (race)

**위치**: HandleMenuCommand 의 updateConfig 람다 — 1125-1166행, 특히 마지막 문장 1165행 `_config = Settings.Save(_config);`

람다는 `_config = ThemePresets.Apply(newConfig)` (1130) 이후 ClearProfileCache / I18n.Load / ImeStatus.UpdateDetectionMethod / Overlay.HandleConfigChanged / ApplyCursorConfigChange / ShowIndicatorAtForeground / ApplyTrayEnabledTransition 를 **전부 먼저** 실행하고, `Settings.Save` 는 **맨 마지막**에 부른다. Save 는 디스크가 앞서 있으면 3-way 병합한 결과를 돌려주는데(§N-48), 그 값이 도착했을 때는 모든 적용자가 이미 병합 전 인스턴스로 실행을 마친 뒤다. 게다가 Save 는 `_lastMtime` 을 self-bump(JsonSettingsManager.cs:200) 하고 병합 시 TryLoad 로 한 번 더 갱신하므로 **CheckReload 가 영영 발화하지 않아 재적용 기회도 없다**. 결과: `Program._config`·디스크는 병합 값, 그러나 I18n 언어·Overlay._config(HandleDpiChanged/HandleMoving/GetDefaultPosition 이 읽음)·CursorOverlay._config·OverlayAnimator._config·트레이 HICON 은 병합 전 값에 영구 고정. 형제 경로인 HandleTrayToggle 은 Save 를 **먼저**(1082행) 두고 적용자를 뒤에 두므로 정반대다 — 순서 불일치가 결함의 증거다. 재현: config.json 을 외부 편집기로 열어 `language` 를 ko→en, `label_height` 를 변경 후 저장 → 5초 폴링 전에 트레이 메뉴에서 '애니메이션 사용' 토글 → 람다


### N48 — `Program.cs` (robustness)

**위치**: HandleConfigChanged 926-933행 (`else if (!_config.UserHidden) RefreshVisibleIndicator();`)

핫리로드의 UserHidden 전이 처리가 **한 방향만** 있다. false→true 는 926-931행이 즉시 HideOverlay 로 닫지만, true→false 는 933행의 RefreshVisibleIndicator 로 떨어지는데 이 헬퍼는 `if (_indicatorVisible && _lastForegroundHwnd != Zero)` 가드가 걸려 있고(Program.SystemEvents.cs:36) 숨김 상태에서는 `_indicatorVisible == false` 라 **무조건 no-op** 이다. 같은 전이를 트레이 좌클릭(HandleTrayToggle→ApplyUserHiddenTransition:1103)과 메뉴 토글(1148-1152)은 TryShowIndicatorIfForegroundAllowed 로 정상 복원하므로 세 경로 중 리로드만 비대칭이다. 감지 스레드도 구제하지 못한다 — WM_POSITION_UPDATED 는 `foregroundChanged` 일 때만 post 되고(DetectionService.cs:420-425), 편집기에 포커스를 둔 채 저장하면 포그라운드가 안 바뀐다. 재현: config.json 에 `"user_hidden": true` 로 배지를 숨긴 뒤, 메모장에서 같은 파일을 `false` 로 고쳐 저장하고 **포커스를 메모장에 그대로 둔다** → 5초 뒤 로그에 'Config reloaded' 가 찍히고 트레이 메뉴의 '플로팅 배지 숨김' 체크도 풀리지만 배지는 나타나지 않는다. Alt+Tab 이나 한/영 토글을 해야 비로소 표시.


### N49 — `Program.SystemEvents.cs` (robustness)

**위치**: HandleSettingChange 48-74행 (56-63행에서 ThemePresets.Apply + Overlay.HandleConfigChanged, 72-73행 Tray.UpdateState — CursorOverlay 갱신 없음)

WM_SETTINGCHANGE / WM_THEMECHANGED / WM_DWMCOLORIZATIONCOLORCHANGED 는 `theme: system` 일 때 `_config = ThemePresets.Apply(_config)` 로 6쌍 색상을 DWM colorization 기준으로 재계산하고(App/Config/ThemePresets.cs:52 → ApplySystemTheme, 134행 TryGetColorizationRgb), Overlay 엔진과 트레이 HICON 은 새 인스턴스로 갱신한다. 그러나 **커서 헤일로만 누락**이다 — CursorOverlay 는 자체 `_config` 필드를 들고 있고(App/UI/CursorOverlay.cs:41) 갱신 진입점은 `ApplyCursorConfigChange` → `CursorOverlay.HandleConfigChanged` (Program.Timers.cs:85) 뿐인데 이 경로가 HandleSettingChange 에서 호출되지 않는다. BuildStyle(App/UI/CursorOverlay.cs:446-471)·SetImeState·SetCapsLock 이 모두 그 stale `_config.HangulBg/EnglishBg/NonKoreanBg` 를 읽으므로, 다음 config.json 리로드나 트레이 토글이 있을 때까지 **영구히** 옛 테마 색으로 그려진다(_config 인스턴스가 안 바뀌므로 자가치유 없음). 재현: `"theme": "system"`, `"cursor_indicator_enabled": true` 로 실행 → Windows 설정에서 라이트↔다크(또는 강조 색) 전환 → 플로팅 배지와 트레이 아이콘은 즉시 새 색, 커서 헤일로만 이전 색 유지. 트레이 메뉴에서 아무 항목이나 토글하면 그때 따라


### N50 — `Core/Logging/Logger.cs` (lifetime)

**위치**: EnqueueToFile 218-227행 (`if (_drainThread is null)` 분기) + Initialize 103행 (`if (!enabled) return;`)

`Initialize(enabled:false, …)` 는 StopDrainThread 후 즉시 반환해 `_drainThread` 를 null 로 남긴다. EnqueueToFile 는 `_drainThread is null` 을 '아직 부팅 중(pre-init)' 으로만 해석해 모든 라인을 `_preInitBuffer` 에 넣는데, 이 버퍼의 **유일한 소비자는 Initialize 끝의 FlushPreInitBuffer(162행)** 다. 즉 `log_to_file: false` 이면 프로세스 수명 내내 로그가 소비자 없는 큐에 쌓여 MaxQueueSize(10,000) 문자열을 상주시키고, 그 뒤에는 호출마다 `_preInitBuffer.Count` 세그먼트 워크 + TryDequeue + Interlocked.Increment 를 도는 트레드밀이 된다. 감지 루프가 80ms 폴링으로 Debug/Warning 을 뿜는 핫패스라 축적 속도가 빠르다. 런타임 전환(config.json 에서 log_to_file 을 true→false 로 리로드)도 같은 상태로 들어간다(Program.cs:908-913 이 Logger.Initialize 재호출). `_drainThread` 가 volatile 도 아니라 다른 스레드가 stale 하게 읽어 Initialize 직후에도 pre-init 경로로 새는 좁은 창이 추가로 존재한다. 재현: config.json 에 `"log_to_file": false`, `"log_level": "debug"` 로 실행 후 수 분 방치 → 프로세스 관리 힙에 `_preInitBuffer` 문자열 1만 개가 상주(작업 관리자 메모리 증가 후 정체), `_preInitDroppedCount` 는 계속 증가하지만 어디에도 기록되지 않음. `log_to_file: t

