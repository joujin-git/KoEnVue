# AUDIT 2026-07-30 — 동시성·재진입·수명 감사 (v1.0.0.0 릴리즈 보류 근거)

**출처**: `/bug-hunt` 멀티에이전트 워크플로우 (2026-07-29 21:00~2026-07-30 00:20, 5 라운드, 에이전트 296개 / 완료 266 · 실패 30, 서브에이전트 토큰 25.8M).
**대상**: `App/` 와 `Core/` 의 2-스레드 경로 전체 (메인 메시지 루프 + 감지 루프 + OS 이벤트 콜백).
**판정 방식**: finder 4종(shared-state / reentrancy / lifetime / config-race)이 라운드마다 병렬 탐색, 새 결함마다 3개 렌즈(correctness / reproducibility / concurrency-theory)로 교차검증해 **2표 이상**만 확정.

**결론**(2026-07-30 시점): v1.0.0.0 정식 배포를 **보류**한다. 사용자 조작으로 프로세스가 죽는 경로(§A)가 확인됐고, 사용자 설정이 조용히 소실되는 경로(§B·§G)가 함께 나왔다. 모두 선재 결함이며 이번 아이콘·좌클릭 변경과는 무관하다.

> **진행 상황** — 보류 사유였던 **§A 는 2026-08-01 해결**(묶음 1 완료). 이후 판단은 [§5 현재 상태](#현재-상태-2026-08-01-갱신) 참조.

---

## 1. 규모 — 확정 67건 = 고유 결함 14그룹

워크플로우의 중복 제거 키는 `파일 + kind + desc 앞 60자` 라서, **같은 결함을 라운드·finder 마다 다르게 서술하면 별건으로 집계된다.** 실제로 67건을 병합하면 고유 결함은 **14그룹**이다. 아래 표의 "원본" 열이 67건 중 어느 항목이 그 그룹으로 접히는지의 매핑이다.

| 그룹 | 결함 | 원본 항목 | 우선순위 | 검증 |
|------|------|-----------|----------|------|
| **A** ✅ | 모달 다이얼로그 3종이 재진입 가드보다 먼저 static 을 파괴 → 프로세스 종료 | 6·15·31·37·38·40·58 (7) | ~~**P0**~~ **해결 2026-08-01** | ✅ 메인 직접 재현 |
| **B** ✅ | 모달 중 `WM_CONFIG_CHANGED` 재진입 → 다이얼로그 스냅샷이 핫리로드를 되돌림 | 2·7·8·19·25·32·46·52·57·67 (10) | ~~**P1**~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **C** ✅ | `Logger` drain 스레드 Join 실패 후 좀비 부활 + `_fileWriter` 동시 Dispose → 프로세스 종료 | 3·14·16·26·33·41·49·53·61·62 (10) | ~~**P1**~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **G** ✅ | config 파싱 실패 시 전 필드 디폴트를 메모리에 올리고 디스크에 저장 → 사용자 설정 전멸 | 21·45·51 (3) | ~~**P1**~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **D** ✅ | 프로필 LRU 캐시가 "락 밖 계산 → 무효화 이후 삽입" → 영구 stale + LRU desync | 1·9·20·24·29·47 (6) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **E** ✅ | 캐시 무효화가 렌더보다 늦게 실행돼 같은 람다 안에서 옛 global 이 쓰임 | 10·54·66 (3) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **F** ✅ | `Save` 의 mtime self-bump 가 2단계라 자기 저장을 외부 편집으로 오인 (TOCTOU) | 4·36 (2) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **H** ✅ | 윈도우 클래스는 부팅 시 1회 등록인데 커서 헤일로는 lazy 생성 → 핫리로드로 클래스명이 바뀌면 미등록 클래스로 `CreateWindowExW` | 12·17·22·64 (4) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **K** ✅ | `tray_enabled` 런타임 전이 미처리 → 트레이 셸 등록·HICON 이 해제되지 않거나 영영 생성 안 됨 | 18·43 (2) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **M** ✅ | 시스템 sizemove 모달 루프 중 애니메이션 타이머가 드래그 중인 배지를 숨기고 복구 실패 | 11·30 (2) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **L** ✅ | `_hwndPositions` 가 원시 HWND 를 영구 키로 사용 + 제거 코드 없음 → 핸들 재활용 오식별 + 단조 증가 | 44·63 (2) | ~~P2~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **I** ✅ | 종료 핸드셰이크 `Join(500)` 이 감지 루프 최대 sleep 보다 짧아 주석이 막는다고 한 race 가 열려 있음 | 23·28·35·65 (4) | ~~P3~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **J** ✅ | `_vdmFailCount++` 비원자 — "감지 스레드 단일 라이터" 근거 주석이 PR-26 이후 무효 | 5·27·56 (3) | ~~P3~~ **해결 2026-08-01** | 2/3 투표 → 메인 재확인 |
| **N** | 단건 9종 (아래 §N) | 13·34·39·42·48·50·55·59·60 (9) | P2~P3 | 2/3 투표 |

## 2. 커버리지 한계 — 이 목록은 하한선이다

**세션 토큰 한도로 에이전트 30개가 실패했다.** 실패 분포가 고르지 않다는 점이 중요하다.

- **라운드 4·5 의 finder 4종이 전멸** (`hunt:lifetime#4` ~ `hunt:config-race#5` 8개) — 마지막 두 라운드는 **탐색이 아예 수행되지 않았다.** 워크플로우는 "새 결함 0"으로 읽고 dry 카운터를 올려 종료했으므로, **정상 수렴이 아니라 자원 소진으로 멈춘 것이다.**
- **검증 22건 실패** — 3렌즈 중 일부가 죽은 결함은 2표를 못 채워 confirmed 에서 탈락했다. 즉 실재하는 결함이 목록에서 빠졌을 수 있다(false negative).

따라서 "이제 다 나왔다"고 볼 수 없다. 그룹 A~C 를 처리한 뒤 **라운드를 새 세션에서 재개**해야 한다.

## 3. 검증 신뢰도 — 두 등급을 구분한다

- **✅ 메인 직접 재현** — 메인 세션이 해당 파일을 열어 인용된 줄과 인과 사슬을 전부 확인. 현재 §A 뿐이다.
- **2/3 투표** — finder 1 + 검증 렌즈 2표 이상. 근거는 구체적이지만 **메인이 재확인하지 않았다.** 착수 전 해당 파일을 직접 읽어 전제를 확인할 것. 서브에이전트가 인용한 줄 번호·조건은 종종 인접 코드로 어긋난다.

---

## A. 모달 다이얼로그 재진입 → 프로세스 종료 (P0, 메인 확인) — ✅ 해결 2026-08-01

> **처리 결과** — 아래 「수정 방향」 2안을 모두 적용했다. 판정은 `ModalDialogLoop.RejectReentry()` 단일 구현이고(P4), 호출처는 다이얼로그 3종 `Show()` **첫 문장** + `Tray.ShowMenu`(1차 방어) + `DialogShell.Run`(2차 방어) 5곳. 회귀 가드는 `ModalReentryGuardTests` 5케이스로, **가드를 빼면 3케이스가 실패하는 것을 대조군으로 실측 확인**했다(반환값이 아니라 정적 필드 보존을 검사하므로 가드가 프롤로그 뒤로 밀려도 잡힌다). 설계 근거는 [implementation-notes.md](../implementation-notes.md) 의 `RejectReentry` 항목, invariant 는 [conventions.md](../conventions.md) P6 블록의 grep 2줄. 아래 원 분석은 기록으로 보존한다.

**증상**: 다이얼로그를 열어둔 채 트레이를 다시 조작하면 앱이 죽는다.

**재현**: 트레이 우클릭 → 「위치 기록 정리」(위치 기록 1개 이상 필요) 또는 「상세 설정」 → 창을 **닫지 않은 채** 트레이 우클릭 → 같은 항목 선택 → 원래 창에서 「확인」/「삭제」 클릭 → 프로세스 즉시 종료.

**원인 사슬** (전부 코드로 확인):

1. [DialogShell.cs:101](../../Core/Windowing/DialogShell.cs:101) — 재진입 가드가 `Run` **내부**에 있다. `ModalDialogLoop.IsActive` 면 포커스만 복원하고 `false` 반환.
2. [CleanupDialog.cs:71-78](../../App/UI/Dialogs/CleanupDialog.cs:71) — `Show` 가 `Run` **호출 전에** 공유 static 을 전부 리셋한다(`_items`, `_selectedItems`, `_dlgClosed`, `_checkboxHandles`, `_scrollPos`, `_scrollMax`). 가드는 아직 평가되지 않았다.
3. [CleanupDialog.cs:116-121](../../App/UI/Dialogs/CleanupDialog.cs:116) — `Run` 이 `false` 를 반환해도 에필로그가 그대로 실행돼 `_hwndDialog = 0`, `_hwndViewport = 0`, **`_items = null!`** 까지 간다.
4. 살아 있던 첫 다이얼로그에서 확인을 누르면 `CommitSelections` 의 `_items` 참조가 `NullReferenceException`. `CleanupDlgProc` 는 `[UnmanagedCallersOnly]` 이고 conventions §11 이 WndProc 을 의도적으로 try/catch 예외로 두므로, 관리 예외가 `DispatchMessageW` 경계를 넘어 **NativeAOT 가 프로세스를 종료**한다.

**모달 중에 트레이 메뉴가 열리는 이유** — 이 전제가 성립하지 않으면 결함이 아니므로 따로 확인했다.

- [ModalDialogLoop.cs:59](../../Core/Windowing/ModalDialogLoop.cs:59) `EnableWindow(hwndOwner, false)` 는 **마우스·키보드 입력만** 막는다. posted message 디스패치는 막지 못한다.
- [ModalDialogLoop.cs:68](../../Core/Windowing/ModalDialogLoop.cs:68) `GetMessageW(out msg, IntPtr.Zero, 0, 0)` — 필터가 `IntPtr.Zero` 라 **스레드의 모든 창 메시지**를 받는다. `IsDialogMessageW` 는 대상이 다이얼로그가 아니면 `false` 를 돌려주므로 그대로 `DispatchMessageW` 로 넘어간다.
- 트레이 아이콘은 explorer 소유이므로 사용자는 계속 클릭할 수 있고, explorer 가 `hwndMain` 으로 `WM_TRAY_CALLBACK` 을 post 한다.
- [Program.cs:427](../../Program.cs:427) — `WM_TRAY_CALLBACK` 분기에 모달 게이트가 없다. 바로 `HandleTrayCallback`.
- [Tray.Menu.cs:22](../../App/UI/Tray.Menu.cs:22) — `ShowMenu` 는 `_initialized` 만 확인하고 `ModalDialogLoop.IsActive` 를 보지 않는다.

**영향 범위**: `CleanupDialog` · `SettingsDialog` · `ScaleInputDialog` 셋 다 같은 구조다. `SettingsDialog` 는 크래시 전에 **설정 유실**이 먼저 나타난다(§B 와 겹침).

**수정 방향** (이중 방어 권장):

1. 각 `Show()` **맨 앞**에 가드를 옮긴다 — static 을 건드리기 전에 `ModalDialogLoop.IsActive` 를 보고 기존 창으로 포커스만 복원하고 반환. `DialogShell.Run` 안의 가드는 그대로 두되(직접 호출자 방어) 실질 판정은 프롤로그가 담당.
2. [Tray.Menu.cs](../../App/UI/Tray.Menu.cs) `ShowMenu` 앞에도 가드를 넣어 **모달 중에는 메뉴 자체가 열리지 않게** 한다. 다이얼로그 3종이 전부 트레이 메뉴 경유이므로 이 한 줄이 진입 자체를 차단하고, 모달 중 다른 메뉴 항목(색상·투명도·종료)이 실행되는 부수 위험도 함께 막는다.

**회귀 가드**: `ModalDialogLoop.IsActive` 가 참일 때 각 `Show()` 가 static 을 건드리지 않고 조기 반환하는지 단위 테스트로 고정. 순수 판정 부분을 분리해야 테스트 가능하다.

---

## B. 모달 중 config 재진입 → 핫리로드 lost update (P1) — ✅ 해결 2026-08-01

> **처리 결과** — 두 수정 방향 중 **"필드 병합 커밋"**(근본안)을 택했다. 다만 필드별 diff 를 새로 짤 필요는 없었다 — `SettingsDialog.TryCommit` 이 이미 **모든 필드를 컨트롤에서 다시 읽어 베이스 위에 덮는** 구조라, 베이스를 `_initialConfig`(열릴 때 스냅샷)에서 `currentConfig()`(커밋 시점 현재값)로 바꾸는 것만으로 의도한 의미론이 나온다: 다이얼로그가 노출하는 필드는 사용자가 화면에서 본 값이 이기고, 노출하지 않는 필드(위치 기록·앱 프로필)는 최신값이 살아남는다. 같은 한 줄(`config = currentConfig();`)이 `Tray.HandleMenuCommand` 의 `config with { … }` 24곳도 한꺼번에 고친다 — `TrackPopupMenu` 역시 자체 모달 루프라 메뉴가 열린 동안 재진입이 성립하기 때문.
>
> **"모달 중 보류" 안은 채택하지 않았다** — 보류해도 커밋이 먼저 디스크를 덮은 뒤 보류분이 처리되므로 lost update 의 순서만 바뀔 뿐 해소되지 않는다. 회귀 가드는 `ConfigCommitBaseTests`(베이스를 스냅샷으로 되돌린 대조군에서 2건 실패 실측).

**증상**: 설정 다이얼로그가 열려 있는 동안 `config.json` 을 외부에서 편집하면, 다이얼로그 확인 시 그 편집이 **조용히 되돌려지고 디스크에도 옛 값이 덮인다.**

**사슬**: [DetectionService](../../App/Detector/DetectionService.cs) 의 `Settings.CheckConfigFileChange` 호출이 `TryHandleModalGate` **앞**에 있어 모달 게이트 보호를 받지 못한다 → `WM_CONFIG_CHANGED` 가 모달 중에도 post 된다 → 중첩 루프가 디스패치 → `HandleConfigChanged` 가 `_config` 를 새 인스턴스로 교체 → 다이얼로그 확인 시 [Program.cs:983](../../Program.cs:983) 이 **열릴 때 잡은 스냅샷**으로 `_config` 를 덮고 [Program.cs:1011](../../Program.cs:1011) `Settings.Save` 로 디스크까지 덮어쓴다.

같은 창에서 `HandleMenuCommand(commandId, config, …)` 의 `config` 인자도 stale 이 되어 `IDM_SIZE_CUSTOM` / `IDM_CLEANUP` 의 `config with { … }` 가 옛 베이스 위에 얹힌다.

**주의**: §F(mtime TOCTOU)가 이 결함의 트리거를 늘린다 — 사용자가 `config.json` 을 건드리지 않아도 앱 자신의 저장이 `WM_CONFIG_CHANGED` 를 유발할 수 있다.

**수정 방향**: 모달 중에는 `WM_CONFIG_CHANGED` 를 **큐에 보류**하고 모달 종료 후 1회 처리하거나, 커밋을 스냅샷 통째 대입이 아니라 **다이얼로그가 실제로 바꾼 필드만 현재 `_config` 에 병합**하도록 바꾼다. 후자가 근본적이다.

## C. Logger drain 스레드 — 좀비 부활 + 동시 Dispose → 프로세스 종료 (P1) — ✅ 해결 2026-08-01

> **처리 결과** — 아래 사슬 5개를 메인이 코드로 재확인했고(2/3 투표 등급이었으나 전제가 전부 사실이었다) 수정 방향의 **세대 토큰** 안을 채택했다. `_shutdownRequested` 를 `int _generation` 으로 대체(단조 증가 → 좀비가 되살릴 수단 없음) + `_fileWriter`/`_filePath`/`_maxSizeBytes` 를 `_writerLock` 한 묶음으로 보호. Join 타임아웃 후에는 `Monitor.TryEnter(1s)` 로 상한을 두고 획득 실패 시 **Dispose 를 포기**한다 — 강제 Dispose 가 바로 이 결함의 원래 사인이기 때문. 설계 근거는 [implementation-notes.md](../implementation-notes.md) 의 「Logger 재초기화」 절, 회귀 가드는 `LoggerReinitTests`.

**증상**: `config.json` 의 `log_to_file` / `log_file_path` / `log_max_size_mb` 를 바꾼 뒤 앱이 죽거나 로그가 영구히 기록되지 않는다.

**사슬**: 로그 설정 변경 → `HandleConfigChanged` → [Logger.cs](../../Core/Logging/Logger.cs) `Initialize` → `StopDrainThread`.

- `_drainThread.Join(3000)` 이 **타임아웃해도 그대로 진행**한다(코드가 `!joined` 분기를 명시해 저자도 발생 가능성을 인정). 회전 구간의 `File.Move`/`File.Delete` 가 AV 스캔·네트워크 경로·tail 뷰어에 막히면 3초를 넘긴다.
- `_drainThread = null` 을 찍으므로 이후 `EnqueueToFile` 이 살아 있는 drain 스레드를 못 보고 `_preInitBuffer` 로 우회 → **로그 영구 미기록**.
- `Initialize` 가 `_shutdownRequested = false` 로 되돌리면 좀비의 `while (!_shutdownRequested)` 루프가 **영구 부활**하고, 두 번째 drain 스레드가 추가된다.
- 그 뒤 메인의 `FlushQueue()` + `_fileWriter?.Dispose()` 가 살아 있는 스레드의 `WriteLine` 과 겹친다. `StreamWriter` 는 스레드 안전하지 않아 버퍼가 깨지고, `ObjectDisposedException` / `NullReferenceException` 은 `catch (IOException or UnauthorizedAccessException)` **필터 밖**이라 백그라운드 스레드를 뚫고 나가 프로세스를 종료한다.
- `_filePath` 와 `_fileWriter` 가 한 쌍으로 갱신되지 않아 "옛 writer + 새 `_filePath`" 조합으로 `File.Move` 가 방금 만든 새 로그를 `.old` 로 밀어버리는 torn-pair 도 성립한다.

**수정 방향**: `_fileWriter`/`_filePath`/`_maxSizeBytes` 를 하나의 lock 으로 묶고, Join 실패 시 **재초기화를 포기**하거나(옛 writer 유지) 좀비가 스스로 끝나도록 세대 토큰을 도입한다. `_shutdownRequested` 를 좀비가 되살릴 수 없게 세대별 플래그로 바꾸는 것이 핵심.

## G. config 파싱 실패 → 전 필드 디폴트를 디스크에 저장 (P1) — ✅ 해결 2026-08-01

> **처리 결과** — `TryLoad(out T)` 로 실패를 호출자에게 알리고, `HandleConfigChanged` 는 실패 시 기존 `_config` 를 유지한 채 물러난다. 안내는 연속 실패당 1회(`_configReloadFailed` 래치). **"저장 경로를 잠그는 것도 검토"는 채택하지 않았다** — 트레이 조작이 조용히 무시되는 다른 혼란을 만들기 때문이며, 대신 안내 문구가 "지금 설정을 바꾸면 편집 중인 내용이 덮어써진다"를 알린다(근본 해결은 §N-48 read-modify-write 쪽).
>
> **회귀 테스트 중 별건 발견** — `config.json` 최상위가 객체가 아니면(`null`·배열·스칼라) 병합 단계가 `JsonElementWrongTypeException` 을 던지는데 이 타입이 로드 예외 필터 밖이라 **프로세스가 종료**됐다. 병합 진입점에 최상위 `ValueKind` 가드를 넣어 손상으로 분류. 이 건은 원래 67건 목록에 없었다 — bug-hunt 의 커버리지 한계(§2)를 보여주는 사례다.

**증상**: `config.json` 이 잠깐이라도 파싱 불가 상태(편집 중 저장, 인코딩 사고)로 읽히면 **모든 설정이 디폴트로 초기화되고 그 상태가 디스크에 영구 저장**된다.

**사슬**: 파싱 실패 시 `Load` 가 `return new T()`(전 필드 디폴트)를 돌려주는데, `HandleConfigChanged` 가 성공/실패를 구분하지 않고 `_config = Settings.Load()` 로 대입한다. 이후 어떤 저장 경로(트레이 토글·드래그 종료)든 실행되면 디폴트가 파일에 쓰인다.

**수정 방향**: `Load` 가 실패를 **호출자에게 알리고**(`TryLoad` 또는 sentinel), 실패 시 기존 `_config` 를 유지하며 사용자에게 1회 알린다. 실패 상태에서는 저장 경로를 잠그는 것도 검토.

## D·E·F. 프로필 캐시 / 저장 억제 (P2)

- **D** ✅ **해결 2026-08-01** (세대 스탬프 + raced 삽입 병합) — `ResolveForApp` 이 캐시 미스 시 **락 밖에서** `MatchProfile` 을 수 ms 계산한 뒤 다시 락을 잡아 삽입한다. 그 사이 메인 스레드가 `ClearProfileCache` 를 완료하면 **옛 global 로 머지된 결과가 무효화 이후에 꽂혀 영구 stale** 이 된다. 캐시 키에 config 세대 정보가 없어 자기치유도 없다. 조회 락과 삽입 락이 분리돼 같은 키 중복 삽입으로 `_profileLruOrder` 와 `_profileCache` 가 desync 되는 경로도 열려 있다. **체감**: config 를 저장했는데 특정 앱에서만 옛 색·크기가 계속 나온다.
- **E** ✅ **해결 2026-08-01** (무효화를 `_config` 게시 직후로 이동) — 메뉴/설정 람다가 캐시 무효화를 **맨 마지막**에 해서, 같은 람다 안의 렌더가 옛 값을 쓴다. 순서만 바꾸면 된다.
- **F** ✅ **해결 2026-08-01** — `Save` 가 파일 쓰기와 `_lastMtime` 갱신 사이에 틈이 있어, 감지 스레드가 **자기 저장을 외부 편집으로 오인**해 불필요한 전체 리로드를 유발한다(배지 깜빡임 + `HandleSettingChange` 의 메모리 전용 변경 소실). §B 의 트리거를 늘리는 것이 더 큰 문제.

## H·K·M·L. 수명·전이 누락 (P2)

- **H** ✅ **해결 2026-08-01** (등록한 클래스명을 `_registeredOverlayClassName` 에 고정 + 변경 시 경고 1회) — 오버레이 윈도우 클래스는 부팅 시 `overlay_class_name` 으로 1회 등록되는데 커서 헤일로 창은 lazy 생성이다. 핫리로드로 클래스명이 바뀌면 **등록되지 않은 클래스로 `CreateWindowExW`** 를 호출해 실패한다.
- **K** ✅ **해결 2026-08-01** (`ApplyTrayEnabledTransition` — 리로드·설정다이얼로그 양 경로) — `tray_enabled` 의 런타임 전이를 아무도 처리하지 않는다. true→false 에서 셸 등록·HICON 이 해제되지 않고, false→true 에서 영영 생성되지 않는다.
- **M** ✅ **해결 2026-08-01** (`Hide()` 를 드래그 중 보류 → `EndDrag` 가 적용) — 시스템 sizemove 모달 루프(`DefWindowProc` 내부 자체 펌프) 중 애니메이션 타이머가 계속 돌아 **드래그 중인 배지를 숨기고**, 드래그 종료 경로가 복구하지 못한다.
- **L** ✅ **해결 2026-08-01** (값에 프로세스명 동반 + 상한 초과 시 죽은 창 prune) — `_hwndPositions` 가 원시 HWND 를 영구 키로 쓰고 **제거 코드가 전혀 없다**. 커널이 핸들 값을 재발급하면 다른 창이 죽은 창의 좌표를 물려받고, 이 경로가 프로세스명 기반 저장보다 **우선순위가 높아** 새 앱의 저장 위치를 덮는다. 상주 앱이라 항목은 단조 증가한다.

## I·J. 측정·종료 경로 (P3)

- **I** ✅ **해결 2026-08-01** (주석 정정 + 타임아웃 상수화 + 타임아웃 시 Debug 로그. **타임아웃 값은 의도적으로 유지** — 아래 참조) — `OnProcessExit` 의 `Join(500)` 이 감지 루프 최대 sleep 보다 짧아, 주석이 "차단한다"고 주장하는 race(hwnd 파괴 ↔ `PostMessageW`)가 실제로는 열려 있다. **주석이 사실과 다른 것**이 실질 문제다.
- **J** ✅ **해결 2026-08-01** (`Interlocked.Increment` 반환값으로 게이트 + 라이터 2개 명시) — `_vdmFailCount++` 가 비원자인데 근거 주석("감지 스레드 단일 라이터")은 PR-26 이후 무효다. [Program.cs:690](../../Program.cs:690) 경로로 메인 스레드도 증가시킨다. lost update 로 로그 게이트가 건너뛰어져 **dev-note 가 마이그레이션 판단 근거로 삼기로 한 누적 카운트가 과소 집계**된다. `Interlocked.Increment` 반환값으로 게이트를 평가하면 끝.

## N. 단건 9종 (P2~P3)

| # | 파일 | 요지 |
|---|------|------|
| 13 | `App/Startup/StartupTaskManager.cs` | 부팅 후 ~8초 백그라운드 schtasks 동기화와 트레이 토글이 같은 태스크를 락 없이 동시 수정 |
| 34 | `Program.cs` | 애니메이션 타이머가 오버레이를 숨길 때 `_indicatorVisible` 미갱신 → 메인·감지 로직이 영구 불일치 |
| 39 | `Core/Windowing/LayeredCursorBase.cs` | DIB 섹션이 `_memDC` 에 select 된 채 Dispose — GDI 객체 해제 순서 |
| 42 | `Program.Bootstrap.cs` | 메인 윈도우가 정상 루프 종료 전 파괴되는 경로에서 `_hwndMain`/`_hwndOverlay` 미리셋 |
| 48 | `Program.cs` | 모든 저장이 디스크 대조 없는 read-modify-write → 외부 편집 소실 |
| 50 | `App/Detector/ImeStatus.cs` | WinEvent 콜백은 **global** detection_method 로 분류, 감지 루프는 per-app resolved 로 분류 → 같은 창을 다르게 판정 |
| 55 | `Program.cs` | 같은 IME 전이 1회에 `WM_IME_STATE_CHANGED` 가 2회 도착하는데 수신부에 멱등 가드 없음 |
| 59 | `Program.SystemEvents.cs` | `_config` 새 인스턴스 게시와 캐시 무효화의 순서 |
| 60 | `Program.OverlayDrag.cs` | `WM_NCLBUTTONDOWN`/HTCAPTION 승격 경로의 재진입 |

---

## 4. 처리 계획

| 묶음 | 내용 | 기대 효과 |
|------|------|-----------|
| ~~**1**~~ ✅ | §A — 다이얼로그 3종 프롤로그 가드 + `ShowMenu` 가드 | **완료 2026-08-01** — 사용자 조작으로 죽는 경로 제거. v1.0.0.0 재개 조건 충족 |
| ~~**2**~~ ✅ | §G + §C — config 로드 실패 격리, Logger 세대 토큰 + lock | **완료 2026-08-01** — 설정 전멸·프로세스 종료 제거. 회귀 테스트 중 config 최상위 비객체 크래시 1건 추가 발견·수정 |
| ~~**3**~~ ✅ | §B + §F — 모달 중 config 이벤트 보류 또는 필드 병합 커밋, mtime 억제 원자화 | **완료 2026-08-01** — 커밋 베이스를 "지금 값" 으로 전환(보류 방식은 미채택), Save 쓰기+mtime 원자화 |
| ~~**4**~~ ✅ | §D + §E — 캐시 채움을 무효화에 대해 원자화, 무효화 순서 교정 | **완료 2026-08-01** — 세대 스탬프 + 동시삽입 시 LRU 중복 차단, 무효화를 렌더 앞으로 이동 |
| ~~**5**~~ ✅ | §H·§K·§M·§L + §N 상위 | **완료 2026-08-01** — 클래스명 고정 · tray 전이 · 드래그 중 숨김 보류 · HWND 키에 프로세스명 동반+prune (§N 은 별도) |
| ~~**6**~~ ✅ | §I·§J + 사실과 다른 주석 정정 | **완료 2026-08-01** — Join 주석을 사실대로 정정(타임아웃 유지 근거 명시+관측 로그), `Interlocked.Increment` 반환값 게이트 |
| **7** | **새 세션에서 `/bug-hunt` 재개** | §2 커버리지 구멍(라운드 4·5 미탐색) 보완 |

각 묶음 착수 시 **해당 파일을 직접 읽어 전제를 재확인**한다(§3). 묶음마다 회귀 테스트를 추가하고, 순수 판정 로직은 분리해 단위 테스트 가능하게 만든다.

## 5. v1.0.0.0 과의 관계

- 이번 릴리즈 후보(`v0.9.9.7..HEAD`)가 **새로 만든 결함은 하나도 없다.** 14그룹 전부 선재다.
- 같은 검증에서 나온 릴리즈 리뷰 확정 10건은 **이미 수정·커밋됐다**(`93d0360`) — 트레이 아이콘 테마 추종, 아이콘 생성 실패 시 이전 아이콘 유지, 커서 오버레이 초기화 실패 격리, P3/P4 규약 4건, 문서 6곳.
- 따라서 현재 `main` 은 `v0.9.9.7` 보다 개선된 상태다. **보류 사유는 "이번 변경이 위험해서"가 아니라 "1.0 정식이라는 이름에 맞추기 위해 §A 를 먼저 닫기 위해서"였다.**
- 버전은 `0.9.9.7` 유지, CHANGELOG `[Unreleased]` 에 수정분이 누적돼 있다.

### 현재 상태 (2026-08-01 갱신)

**보류 사유였던 §A 는 닫혔다** — 위 §1 표와 §A 절 참조. 문서 첫머리의 "보류한다"는 2026-07-30 시점의 결정 기록이며, 재개 조건 자체는 충족된 상태다.

남은 판단은 사용자 몫이다: **묶음 1 만으로 v1.0.0.0 을 재개**할지, **묶음 2(§G config 로드 실패 격리 + §C Logger)까지 닫고** 갈지. 묶음 2 는 프로세스 종료(§C)와 설정 전멸(§G)을 포함하지만 §A 와 달리 **메인이 직접 재현하지 않은 2/3 투표 등급**이라, 착수 시 해당 파일을 먼저 읽어 전제를 재확인해야 한다(§3). 묶음 7(`/bug-hunt` 재개)은 §2 의 커버리지 구멍이 남아 있으므로 릴리즈 여부와 무관하게 유효하다.

재개 시 절차는 §4 아래 문단 + [release-procedure.md](../release-procedure.md) — csproj `1.0.0.0` · 태그 `v1.0.0.0` · CHANGELOG `[1.0.0.0]` 3곳을 함께 올리고, 재-publish 산출물의 exe + sha256 **쌍을 그대로** 첨부한다(NativeAOT 비결정성).
