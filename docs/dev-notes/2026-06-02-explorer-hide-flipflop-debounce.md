# 2026-06-02 파일 탐색기에서 메인 인디 사라짐 — 시스템 필터 HIDE 디바운스

## 결론

사용자 보고 "파일 탐색기를 클릭하면 메인 인디가 깜박이다 사라진다 — **애니메이션 ON 일 때만**"
를 fix. 근본원인은 애니메이션 엔진이 아니라 **감지 스레드 입력(`hwndFocus`)의 진동**이었다.
시스템 필터 HIDE 에 **N=3 폴링 디바운스**(`DefaultConfig.HideHysteresisPolls`)를 걸어 단발
진동을 흡수해 차단. 1 파일 1 메서드 + 코드 레벨 const 1개, 신규 config 키·P/Invoke 0.

이 증상은 [2026-05-20 post-PR10 fix 시도](2026-05-20-post-pr10-attempts-reverted.md) 의
**회귀 #2/#3 ("부팅 직후 메인 인디 잠깐 보였다 사라짐")** 과 동일 계열이다. 당시 가설 E/F
(애니 엔진 `SnapToTargetAlpha` 가 Fade 타이머 안 죽임 / detection thread 메시지 폭주)로
**5건 연속 수정 실패**하고 전체 revert 했던 고위험 지대. 이번엔 그 미해결을 감지 스레드
입력 진동으로 재규명했다. 그 dev-note 와 상호 링크.

## 증상

| 항목 | 내용 |
|------|------|
| 트리거 | 파일 탐색기(`CabinetWClass`) 클릭/포커스 전환 |
| 증상 | 메인 인디가 깜박이다 사라진 채 박제 (SW_HIDE + Hidden) |
| 조건 | **애니메이션 ON 일 때만** — OFF 면 무증상 |
| 복구 | IME 한/영 토글 (무조건 Show 라 살아남) |

## 근본원인 (3 단계)

### 1. 감지 스레드 입력 진동 (hwndFocus flip-flop)

파일 탐색기는 포커스 전환 직후 `GUITHREADINFO.hwndFocus` 가 `0 ↔ 정상` 으로 진동한다.
그 결과 [`SystemFilter.ShouldHide`](../../App/Detector/SystemFilter.cs) 의 **조건 6**

```csharp
// 6. 키보드 포커스 없음
if (hwndFocus == IntPtr.Zero && config.HideWhenNoFocus) return true;   // SystemFilter.cs:124
```

이 매 폴링 `filtered ↔ non-filtered` 로 뒤집힌다. 감지 스레드는 filter 진입(`!lastFiltered →
filtered`) 마다 `WM_HIDE_INDICATOR`, filter exit 마다 `WM_POSITION_UPDATED` 를 post 하므로
**HIDE 와 Show 가 약 94ms 간격으로 교대 post** 된다.

### 2. 애니메이션 ON 의 FadingOut 박제 (증상이 ON/OFF 로 갈린 이유)

`WM_HIDE_INDICATOR` → `Animation.TriggerHide(forceHidden: true)`. **애니메이션 ON** 이면
Always 모드인데도 `forceHidden` 이 dim-idle 을 우회해 **alpha→0 FadingOut(400ms)** 으로
들어간다. 곧이은 Show(`WM_POSITION_UPDATED`)가 FadingOut 에 재진입하지만, `OverlayAnimator`
의 FadingOut 재진입엔 **`SnapToTargetAlpha` 가 없어** alpha 가 점진 0 으로 수렴한다. 교대
post 의 마지막 이벤트가 HIDE 면 `SW_HIDE` + `Hidden` 으로 박제.

**애니메이션 OFF** 는 즉시 토글(페이드 없음)이라 HIDE 직후 Show 가 즉시 복원 → 잔상 0 →
무증상. 증상이 ON 에서만 난 이유가 이것.

### 3. IME 전환 복구

`WM_IME_STATE_CHANGED` → `TriggerShow(imeChanged: true)` 는 무조건 Show 라, 한/영 토글 1회로
박제가 풀렸다 (사용자가 우회로 인지했던 동작).

## 수정 — HIDE 디바운스 (N=3 폴링)

[`Program.cs`](../../Program.cs) `TryHandleFilter`(감지 스레드):

- `DetectionState.FilteredStreak` int 필드 추가 — 연속 filtered 폴링 수.
- `currentlyFiltered` 면 `streak++`. **연속 `HideHysteresisPolls`(=3) 회 filtered 일 때만**
  HIDE 확정. 그 미만(잠정)은 `Filter HIDE deferred` 로그 후 **상태 갱신 없이 return**.
- non-filtered 진입 시 `FilteredStreak = 0` 리셋.

[`DefaultConfig.HideHysteresisPolls`](../../App/Config/DefaultConfig.cs) `= 3` 는 **AppConfig
키가 아닌 코드 레벨 const** (config.json 오버라이드 불가 → config-reference 비대상).

### 왜 디바운스가 통하는가 — 잠정 구간 상태 미갱신의 비대칭

핵심은 **잠정(`streak < N`) 구간에서 `LastFiltered`/`LastHwndForeground` 등 상태를 갱신하지
않는 비대칭**이다.

- 진동의 한 위상(filtered)에서 잠정이면 상태 불변 → 현 인디 유지.
- 다음 틱이 진동의 반대 위상(non-filtered)이면 filter exit 경로가 `WM_POSITION_UPDATED` 를
  post → Show 자연 복원.
- 따라서 단발 진동은 화면에 아무 변화도 일으키지 못하고 흡수된다.

이 비대칭이 **"첫 진입 Show 누락" 함정을 피하는 이유**이기도 하다. 만약 잠정 구간에서
상태를 갱신해 버리면, 처음 보이려던 인디가 잠정 HIDE 의 부수효과로 상태가 어긋나 Show 가
누락될 수 있다. 갱신을 미루므로 첫 진입 Show 경로는 디바운스의 영향을 받지 않는다. 이는
기존 [Deferred `lastHwndForeground`](../implementation-notes.md#deferred-lasthwndforeground)
(filtered 면 다음 폴링이 foreground 전환을 재시도) 와 같은 결의 설계다.

실제 숨김 대상(작업표시줄·바탕화면 등)은 **연속 filtered** 라 streak 가 N 에 도달해 정상
HIDE 된다 — 비용은 약 `PollIntervalMs × (N − 1)` 의 숨김 지연뿐(80ms 폴링 기준 ~160ms).

### 진단 로깅

잠정(`streak < N`) 구간에서 `Filter HIDE deferred (streak={n}/{N}, fgClass=..., hwndFocus=0x...)`
Debug 로그(`Program.cs` `TryHandleFilter`)를 남겨 진동 흡수를 추적한다. 이 race 영역은
무로깅이면 육안 검증에만 의존하게 되므로(2026-05-20 의 반복 실패 원인 중 하나), HIDE 보류
사유를 로그로 남겨 다음 진단을 돕는다. (진단 중 임시로 넣었던 `HandlePositionUpdated` /
`HandleImeStateChanged` Show 경로 로그는 정리되어 현재 코드에 없다 — `Filter HIDE deferred`
하나만 남김.)

## 검증

- verifier: dotnet build 0/0 + AOT publish 0/0 (exe 4,883,456 bytes, SHA256
  `1181FC18D7A853F04BDFC62E011DBBC5CB1A92FE87511F3C48A9518CD4ADED1D`) + 78/78 PASS.
- reviewer: P1–P6 0 위반. 회귀 매트릭스 5항목 전부 PASS — (1) 작업표시줄 HIDE 정상,
  (2) 첫 진입 Show 누락 방지, (3) flip-flop 흡수, (4) 모달 다이얼로그 게이트 무영향,
  (5) streak 리셋.

## 미해결 잔여 / 후속 주의

1. **FadingOut 재진입 `SnapToTargetAlpha` 부재 (C안 미해결)** — 디바운스는 진동을
   *입력* 단에서 차단할 뿐, `OverlayAnimator` 의 FadingOut 재진입 시 alpha 즉시 스냅이
   없는 구조적 결함 자체는 그대로다. 다른 경로로 HIDE→Show 가 빠르게 교대하면 같은 박제가
   재현될 수 있다. 근본 차원의 fix(FadingOut 재진입에 `SnapToTargetAlpha` 추가)는 이
   고위험 애니 엔진을 건드리므로 본 PR 범위에서 제외 — 디바운스로 알려진 트리거(탐색기
   진동)를 막는 것에 한정했다.

2. **`hwndFocus` 진동 지속시간 미실측** — N=3 이 탐색기 진동을 덮기에 충분한지는 진동
   파형(몇 폴링 동안 몇 번 뒤집히는지)을 실측하지 않았다. 80ms 폴링 × (3−1) ≈ 160ms 창이
   경험적으로 충분했으나, **K=3 충분성은 수동 검증 영역**이다. 진동이 더 길게 지속되는
   환경이 보고되면 N 상향 또는 진동 파형 계측이 필요.

3. **다른 flip-flop 창** — 탐색기 외에 같은 `hwndFocus` 진동을 보이는 셸/UWP 창이 있으면
   동일 디바운스로 자동 흡수되나, 미발견 창은 미검증.

## 절대 다시 시도하지 말 것 (2026-05-20 교훈 재확인)

- **이 증상을 애니 엔진(`SnapToTargetAlpha`/Fade 타이머) 단독 fix 로 접근** — 2026-05-20
  가설 E/F 가 이 방향이었고 5건 연속 실패했다. **감지 스레드 입력(`hwndFocus`)의 진동**
  이라는 *입력 측* 원인을 먼저 의심할 것. 증상이 애니 ON/OFF 로 갈린다고 원인이 애니
  엔진인 것은 아니다 — 애니는 입력 진동을 *증폭*했을 뿐 *발생원*이 아니다.
- **잠정 HIDE 구간에서 상태(`LastFiltered`/`LastHwndForeground`)를 갱신** — "첫 진입 Show
  누락" 회귀를 만든다. 비대칭(미갱신)이 디자인의 핵심.

## 참고

- 동일 계열 회귀의 이전 실패 기록: [2026-05-20-post-pr10-attempts-reverted.md](2026-05-20-post-pr10-attempts-reverted.md)
  (회귀 #2/#3, 가설 E/F).
- 메시지 파이프라인 / 디바운스 메커니즘:
  [implementation-notes.md § Per-poll filter evaluation](../implementation-notes.md#per-poll-filter-evaluation).
- 변경 파일: [`Program.cs`](../../Program.cs) (`TryHandleFilter` + `DetectionState`),
  [`App/Config/DefaultConfig.cs`](../../App/Config/DefaultConfig.cs) (`HideHysteresisPolls`).
