# Core/Windowing 핫 패스 공유 + hwnd 3종 volatile (2026-05-28, PR-18)

> **상태**: PR-18 완료 (5 commit 박음 + verifier/reviewer 양쪽 APPROVE). 설계 박제: [docs/improvement-plan/PR-18.md](../improvement-plan/PR-18.md). 본 dev-note 는 commit 분할 회고 + 두 결정 (premultiply 의미 차이 유지 / ARM64 volatile) 의 근거 명시 + 정직성 (x64 smoke 무회귀 / ARM64 evidence 부재).

## 무엇

5-commit (HEAD: `cedcff6`) 으로 `Core/Windowing/` 의 두 렌더 엔진 (`LayeredOverlayBase` overlay + `LayeredCursorBase` cursor) 의 ~120 LOC 중복 중 50 LOC 를 공유 helper 두 개로 추출 + `Program.cs` 의 hwnd 3종 `volatile` 비대칭 회복.

| commit | 변경 | net LOC |
|--------|------|--------|
| `0dae7be` (1/5) | `Core/Windowing/DibSectionFactory.cs` 신규 (~60 LOC) + `LayeredOverlayBase.EnsureDib` 위임 | +60 / -20 = +40 |
| `af1779e` (2/5) | `LayeredCursorBase.EnsureDib` 위임 (cursor 측 동일 패턴 적용) | -20 |
| `21f1b72` (3/5) | `Core/Windowing/LayeredWindowBlit.cs` 신규 (~40 LOC) + `LayeredOverlayBase` 2 호출 site (`UpdateOverlayDuringDrag` / `UpdateOverlay`) 위임 | +40 / -14 = +26 |
| `a598539` (4/5) | `LayeredCursorBase.UpdateOverlay` 위임 | -7 |
| `cedcff6` (5/5) | `Program.cs` 의 `_hwndMain` / `_hwndOverlay` / `_hwndCursorOverlay` 3종 `volatile` 마킹 + 3줄 코멘트 | +3 / -3 = 0 |

게이트 결과:
- `dotnet build` (Debug) + `dotnet publish -r win-x64 -c Release` (AOT) 양쪽 0 warn / 0 error
- AOT publish 사이즈: **4,861,440 bytes** (PR-17 baseline 동일, ±0)
- 테스트: **40/40 PASS** (회귀 0)
- P1-P6 invariant + §5.2 invariant 13/13 + caller-side 6 항목 × 2 엔진 = 12/12 모두 PASS

## 왜 (결정 1) — premultiply 의미 차이 의도적 보존

PR-18 의 의도적 비범위. `LayeredOverlayBase.ApplyPremultipliedAlpha` ([Core/Windowing/LayeredOverlayBase.cs:715-740](../../Core/Windowing/LayeredOverlayBase.cs#L715)) 와 `LayeredCursorBase.ApplyPremultipliedAlpha` ([Core/Windowing/LayeredCursorBase.cs:264-298](../../Core/Windowing/LayeredCursorBase.cs#L264)) 는 동명 메서드이고 외관상 동일한 픽셀 후처리 (alpha-premultiplied RGB 변환) 를 수행하지만 **`a == 0 && (r | g | b) != 0` 분기에서 의미가 정반대**:

| 엔진 | a==0 && RGB!=0 픽셀 처리 | 의도 |
|------|------------------------|------|
| overlay (`LayeredOverlayBase`) | `ptr[offset + 3] = 255` (alpha 255 복구) | GDI `DrawTextW` AA 가 alpha=0 RGB!=0 픽셀을 정당한 글자 엣지로 출력 → 알파 복구가 올바름 |
| cursor (`LayeredCursorBase`) | `ptr[offset] = ptr[offset+1] = ptr[offset+2] = 0` (RGB 제로화) | `App/UI/CursorRenderer.cs:ShadeDib` 의 2x2 supersampling round-down 부산 픽셀 (`avgAlpha × 255 < 0.5`) — 알파 복구하면 fully-opaque 외곽 잡티 발생 |

[`Core/Windowing/LayeredCursorBase.cs:279-282`](../../Core/Windowing/LayeredCursorBase.cs#L279) 의 인라인 코멘트가 이 의미 차이를 evidence 로 남긴다 — 본 PR-18 의 design draft ([PR-18.md §3](../improvement-plan/PR-18.md#3-비범위-out-of-scope--premultiply-후처리-의미-차이)) 는 cursor 엔진의 외곽 잡티 회귀가 본 코멘트의 유래 ([dev-notes/2026-05-27-cursor-indicator.md "잠재 버그 fix — 외곽 잡티"](2026-05-27-cursor-indicator.md)) 인 점을 명시.

공유화 시도하면:
- 옵션 A — 호출자가 정책 enum 전달 (`PremultiplyPolicy.RestoreAlpha` / `ClearRgb`): helper 안 분기 비용 = 핫 패스 의미 노이즈 + 호출자가 두 의미 모두 알아야 함
- 옵션 B — helper 가 콜백 `Func<byte a, ref byte r, ref byte g, ref byte b>` 받음: 호출당 콜백 디스패치 비용 = AA 처리 픽셀 수만큼 (수천 ~ 수만 회) 핫 패스 회귀

둘 다 ~40 LOC 의 의도적 분기 보존보다 비용 큼. 공유화하지 않는 것이 옳은 설계.

## 왜 (결정 2) — ARM64 NativeAOT 회귀 방어 volatile

[`Program.cs:33-46`](../../Program.cs#L33) 의 전역 상태에서 비대칭이 있었음:

| 필드 | volatile? | cross-thread read | cross-thread write |
|------|---------|------------------|------------------|
| `_config` | ✅ | 감지 스레드 | 메인 스레드 |
| `_lastImeState` | ✅ | 메인 스레드 | 감지 스레드 |
| `_indicatorVisible` | ✅ | 감지 스레드 | 메인 스레드 |
| `_hwndMain` | ❌ → ✅ | 감지 스레드 (8× `PostMessageW(_hwndMain, ...)`) | 메인 스레드 (`CreateMainWindow`) |
| `_hwndOverlay` | ❌ → ✅ | 감지 스레드 (`IsKoenvueWindow`) | 메인 스레드 (`CreateOverlayWindow`) |
| `_hwndCursorOverlay` | ❌ → ✅ | 감지 스레드 (`IsKoenvueWindow`) | 메인 스레드 (`CreateCursorOverlayWindow`) |

x64 의 **TSO (Total Store Order)** 메모리 모델 + 단일 init-then-read 패턴 (window 는 한 번만 생성됨, hwnd 는 lifetime 동안 IntPtr.Zero 또는 안정값) 덕에 회귀 0. PR-19 의 `_detectionThread?.Join(500)` 가 ProcessExit race window 를 좁혔으나, **ARM64 weak memory model + NativeAOT codegen** 조합에서는 init 직후 감지 스레드가 stale `IntPtr.Zero` 를 보고 silent no-op 할 가능성 잔존:

- 메인 스레드: `_hwndMain = User32.CreateWindowExW(...)` 후 감지 스레드 start
- 감지 스레드: 첫 폴링 tick 의 `PostMessageW(_hwndMain, WM_IME_STATE_CHANGED, ...)` — 만약 ARM64 codegen 이 `_hwndMain` read 를 store 보다 먼저로 reorder 하면 stale `IntPtr.Zero` 사용
- `PostMessageW(IntPtr.Zero, ...)` 는 `last-error=ERROR_INVALID_WINDOW_HANDLE` 로 silent fail — 사용자 가시 증상은 "첫 IME 변화 감지 안 됨, 두 번째부터 정상" (재현 매우 어려움)

cost 0 (volatile keyword 6 글자 × 3) 의 방어 패치라 trade-off 자명. `volatile IntPtr` 는 .NET 5+ 에서 64bit native int 까지 합법 — AOT analyzer 경고 0.

## 5-commit 분할 회고 — signature freeze 시점

각 commit 의 의미와 분할 정당화:

### commit 1 → 2: DibSectionFactory signature freeze

commit 1 에서 `DibSectionFactory.TryCreate(memDC, width, height, out SafeBitmapHandle? bitmap, out IntPtr ppvBits) → bool` signature 를 **overlay 측 측에서 먼저** 사용. commit 2 에서 cursor 측이 같은 signature 로 호출 — overlay 측 시그너처가 굳어진 후 cursor 가 진입하는 순서.

대안 (한 commit 으로 두 엔진 동시 위임) 의 위험:
- DibSectionFactory 의 ownership 결정 (raw `HBITMAP` vs `SafeBitmapHandle wrapping`) 이 cursor 측 요구로 바뀌면 overlay 도 동시 수정 — 두 엔진 영역 충돌 위험
- 5-commit 분할 → commit 1 의 caller-side 갱신 6 항목 (`_dibFailureLogged` / `_ppvBits` / `_currentBitmap?.Dispose()` / `_currentBitmap` 교체 / `_currentWidth` / `_currentHeight` / `_lastRenderedStyle = null`) 누락 검출이 한 엔진 범위 안에서 격리 가능

### commit 3 → 4: LayeredWindowBlit signature freeze (동일 패턴)

commit 3 에서 overlay 의 2 호출 site (`UpdateOverlayDuringDrag` + `UpdateOverlay`) 를 **같은 commit** 으로 위임. signature freeze 일관성 — overlay 안에서 두 호출 site 가 같은 signature 를 보장.

commit 4 (cursor 의 1 호출 site `UpdateOverlay` 위임) 가 다음. 같은 패턴.

### commit 5 단독 분리 — 독립 revert 가능성

commit 5 (Program.cs hwnd 3종 volatile) 는 본 PR-18 의 다른 4 commit 과 의미적으로 무관 — Core 추출 회귀가 발견되면 4 commit revert 가능 / volatile codegen 회귀가 발견되면 commit 5 만 revert 가능. 한 commit 으로 묶으면 회귀 시 모든 변경을 함께 revert 해야 함.

### LOC delta 회고

5 commit 합산: +103 LOC (신규 helper 두 파일) / -64 LOC (중복 제거) = net +39 LOC. AOT 사이즈 변동 ±0 bytes — `static class` + 단일 메서드라 vtable 비용 0, 호출자 측 코드 감소 분과 상쇄. NativeAOT 인라인 효과까지 고려하면 net delta 가 0 인 게 자연.

## ARM64 evidence 부재 정직 명시

본 PR-18 의 commit 5 (volatile) 는 **방어적 패치**이며 evidence 가 부재. 진정한 정직:

- **x64 smoke**: 사용자 수동 검증 6 시나리오 매트릭스 (3-상태 토글 / cursor 인디 / 드래그+스냅 / DPI 전환 / multi-monitor / admin 콘솔) 통과 — **회귀 0**
- **ARM64 codegen**: `volatile IntPtr` 가 ILC ARM64 backend 에서 acquire/release barrier 를 올바르게 emit 하는지 코드 리뷰만 — 실 ARM64 머신 publish/실행 evidence 부재 (failure-not-yet-observed)
- **사용자 보고**: 본 PR 진입 전 ARM64 환경에서 "첫 IME 변화 감지 안 됨" 보고 0건 — 회귀 자체가 정의된 적이 없음

본 패치의 정당화는 "cost 0 → 방어 가치 명백" 만으로 충분 — 가설 회귀의 evidence 가 부재해도 추가 비용 없는 안전망이라 정당하다. 미래에 ARM64 환경에서 silent no-op 패턴이 보고되면 본 commit 5 가 1차 의심 영역에서 제외되어 진단 시간 단축.

## 후속 트리거 (재검토 조건)

다음 조건 발생 시 본 PR-18 의 결정 재검토:

1. **premultiply 공유화 재시도** — cursor 엔진과 overlay 엔진의 셰이더 디자인이 통일되어 (예: cursor 가 GDI 그리기로 전환, 또는 overlay 가 distance-field 셰이더로 전환) `a == 0 && RGB != 0` 픽셀의 의미가 한 방향으로 모이면 `ApplyPremultipliedAlpha` 도 공유 helper 로 추출 가능. 현재는 두 셰이더 디자인이 본질적으로 다르므로 분기 보존.
2. **ARM64 실 evidence** — ARM64 환경에서 회귀 보고 또는 silent no-op 패턴 발견 시 본 PR 의 결정 (cost 0 방어 패치) 이 evidence 기반으로 강화. 만약 보고가 5년 이상 0 건이면 코멘트의 "ARM64 weak memory model 회귀 방어" 문구를 "x64 TSO + 단일 init-then-read 패턴이지만 일관성 위해 volatile" 로 약화 가능.
3. **두 helper 의 호출자 추가** — 본 PR 이후 다른 모듈이 `DibSectionFactory.TryCreate` 또는 `LayeredWindowBlit.Blit` 를 호출하면 signature 가 굳어짐. 그 시점에 BLENDFUNCTION 인라인 결정을 재검토 — 새 호출자가 `BlendFlags != 0` 필요 시 오버로드 추가.

## 관련

- 설계 박제: [docs/improvement-plan/PR-18.md](../improvement-plan/PR-18.md)
- LayeredOverlayBase / LayeredCursorBase 의 P4 예외 정당화 + premultiply 의미 차이 발견 경위: [dev-notes/2026-05-27-cursor-indicator.md](2026-05-27-cursor-indicator.md)
- 플로팅 배지 알파 race 영역 (cursor 엔진과 통합 미루기 결정의 1차 근거): [dev-notes/2026-05-27-snap-fade-killtimer.md](2026-05-27-snap-fade-killtimer.md)
- PR-19 의 `_detectionThread?.Join(500)` ProcessExit race window 차단 (본 PR 의 ARM64 방어와 같은 정신): CHANGELOG.md `[Unreleased] § Fixed` 항목
- Core 모듈: [Core/Windowing/DibSectionFactory.cs](../../Core/Windowing/DibSectionFactory.cs) + [Core/Windowing/LayeredWindowBlit.cs](../../Core/Windowing/LayeredWindowBlit.cs)
