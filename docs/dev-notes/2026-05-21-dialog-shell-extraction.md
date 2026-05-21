# DialogShell 추출 — 3 다이얼로그 라이프사이클 통합 + a11y baseline

**Date**: 2026-05-21
**PR**: PR-07 (C3+H4-b)
**Files**: [Core/Windowing/DialogShell.cs](../../Core/Windowing/DialogShell.cs) (신규 200줄), [App/UI/Dialogs/SettingsDialog.cs](../../App/UI/Dialogs/SettingsDialog.cs), [App/UI/Dialogs/CleanupDialog.cs](../../App/UI/Dialogs/CleanupDialog.cs), [App/UI/Dialogs/ScaleInputDialog.cs](../../App/UI/Dialogs/ScaleInputDialog.cs)

## 무엇

세 모달 다이얼로그(`SettingsDialog` / `CleanupDialog` / `ScaleInputDialog`)가 다음 라이프사이클을 손으로 반복:

1. `ModalDialogLoop.IsActive` 가드 → `SetForegroundWindow(ActiveDialog)` early-return
2. `GetCursorPos` → `MonitorFromPoint(MONITOR_DEFAULTTONEAREST)` → `DpiHelper.GetScale/GetRawDpi`
3. 레이아웃 상수 13~25개 `DpiHelper.Scale` 적용
4. `Win32DialogHelper.CalculateNonClientHeight/Width(rawDpi)` 호출
5. 다이얼로그 높이 산출 (대화별 다름 — 일부 fixed, 일부 항목수 기반)
6. `using var hFont = Win32DialogHelper.CreateDialogFont(dpiY)`
7. `Win32DialogHelper.RegisterStandardClass(...)` 호출
8. `Win32DialogHelper.CalculateDialogPosition(hMon, dlgW, dlgH, anchor?)`
9. `CreateWindowExW(WS_CAPTION | WS_SYSMENU, ...)`
10. 자식 컨트롤 생성 (다이얼로그-고유)
11. `ShowWindow(SW_SHOW)` + (선택) `SetForegroundWindow` + (선택) `SetFocus`
12. `try { ModalDialogLoop.Run(...) } finally { DestroyWindow }`

위 12 단계 중 1~9, 11(부분), 12 가 동일 패턴. ~50줄 prologue + ~15줄 epilogue × 3 다이얼로그 = ~195줄 반복. 시간차 결함도 누적:

- **CleanupDialog 의 `SetForegroundWindow` 누락**: 11 단계에서 `ShowWindow` 만 호출하고 명시 포어그라운드 호출이 빠짐. ShowWindow 가 어차피 활성 윈도우라 사용자 체감 변화는 거의 없지만 코드 정합성 결함.
- **`borderW = 16` 매직**: 세 다이얼로그 모두 자식 컨트롤 폭 계산용으로 `int borderW = DpiHelper.Scale(16, dpiScale); int contentW = dlgWidth - pad * 2 - borderW;` 임의 fudge 사용. 실제로는 `nonClientW` (= `2*FIXEDFRAME + 2*PADDEDBORDER`) 와 의미가 가깝지만 값이 정확히 일치하지 않아 1~2 px 어긋남이 누적.

## 왜

P4 (No duplicate impl) — 3-회 반복은 후속 PR 의 회귀 표면. 한 다이얼로그를 손보면 다른 두 곳도 동기화해야 하는 hand-sync 부담. 또한 H4-b (a11y baseline) — `WS_TABSTOP` / `WS_GROUP` 일관 적용은 셸 추출과 동시에 묶어야 효율적 (각 다이얼로그를 만지는 김에 추가).

## 어떻게 — DialogShell API

핵심 결정: **셸은 라이프사이클만, 컨트롤 빌드와 WndProc 는 호출자가**. 시도해 본 대안:

- **셸이 WndProc 까지 흡수**: 다이얼로그-고유 static 상태 참조(`_hwndDialog`, `_hwndViewport`, `_dlgClosed`, `_checkboxHandles`, `_selectAllState`, `_fields`, ...) 가 너무 많아 셸이 generic delegate 로 받아들이려면 인터페이스가 복잡해진다. SELECT_ALL 같은 다이얼로그-고유 명령도 generic 분기 어렵다. **포기**.
- **셸이 자식 컨트롤까지 흡수**: 60 필드 + 12 섹션 + 뷰포트 + 스크롤바 같은 다이얼로그-고유 레이아웃 로직을 fluent builder 로 추상화하려면 별도 빌더 모듈 필요. PR-07 스코프 초과. **포기**.

채택한 형태:

```csharp
DialogShell.Run(
    IntPtr hwndOwner,
    string className,
    delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> wndProc,
    string title,
    int dlgLogicalWidth,
    Func<DialogShellMetrics, int> measureDlgHeight,
    bool useCursorAnchor,
    bool bringToForeground,
    Action<DialogShellContext> buildChildren,
    Action<DialogShellContext>? onAfterShow,
    ref bool isClosedFlag)
    → bool ran
```

2-단계 정보 전달:

- **`DialogShellMetrics`** (`record struct`): `HMonitor` / `CursorPos` / `DpiScale` / `DpiY` / `RawDpi` / `NonClientH` / `NonClientW` / `Pad` / `DlgWidth` + `Scale(int logical)` 메서드. `measureDlgHeight` 콜백이 받아 다이얼로그 높이를 계산. **DPI 메트릭만 알면 되는 단계**.
- **`DialogShellContext`** (`sealed class`): `Metrics` + `HwndOwner` / `HwndDialog` / `HFont` / `DlgHeight` + `ClientW` / `ClientH` 파생 프로퍼티 + `Scale(int)` 위임. `buildChildren` / `onAfterShow` 콜백이 받아 자식 컨트롤 좌표/폰트/모달 종료 플래그 배선.

콜백 분기 매트릭스:

| 다이얼로그 | useCursorAnchor | bringToForeground | onAfterShow | measureDlgHeight |
|---|---|---|---|---|
| SettingsDialog | false (center) | true | null | `m.Scale(DlgHeight)` — fixed 700 logical |
| CleanupDialog | false (center) | true (**fix**) | null | nonClientH + 항목수 기반 |
| ScaleInputDialog | true (cursor) | true | `SetFocus(_hwndScaleEdit) + EM_SETSEL(0,-1)` | nonClientH + label/edit/hint/btn |

`HandleStandardCommands(int wmCommandId, int idOk, int idCancel, ref bool result, ref bool closed, Func<bool>? tryCommit = null) → bool handled`:

- `wmCommandId == idOk || wmCommandId == IDOK` 인 경우 `tryCommit` (null 이거나 true 반환) 통과 시 result=true + closed=true
- `wmCommandId == idCancel || wmCommandId == IDCANCEL` 인 경우 result=false + closed=true
- 그 외 false 반환 (호출자가 자체 분기 처리 — CleanupDialog 의 `IDC_SELECT_ALL` 같은 경우)

`IsDialogMessageW` 가 Enter→IDOK / ESC→IDCANCEL 변환해 보내는 경로와 사용자 클릭 경로 양쪽이 같은 코드로 흐른다.

## a11y baseline (H4-b)

신규 상수 `Win32Constants.WS_GROUP = 0x00020000` ([Core/Native/Win32Types.cs](../../Core/Native/Win32Types.cs)) — `WS_TABSTOP = 0x00010000` 옆에 배치. Win32 표준 값.

- **CleanupDialog**: "전체 선택" + 항목 체크박스 모두에 `WS_TABSTOP` 신규 (v0.9.x 까지 누락). SELECT_ALL 과 첫 항목 체크박스, OK 버튼에 `WS_GROUP` 추가 (그룹 경계 명시).
- **ScaleInputDialog**: STATIC 안내 라벨에 `WS_GROUP` (시작), OK 버튼에 `WS_GROUP`. STATIC 이 EDIT 직전 z-order 라 UIA `LabeledBy` 자동 연결.
- **SettingsDialog**: 12개 섹션 헤더 STATIC + 각 섹션 첫 입력 컨트롤(60 필드 중 12개) + OK 버튼에 `WS_GROUP`. 즉 Tab 키로 컨트롤 간 이동, 화살표 키로 섹션 단위 그룹 이동 가능.

스크린 리더(NVDA / 내레이터) 가 STATIC + 입력 컨트롤 z-order 인접성을 보고 자동으로 라벨 연결. 별도 `WM_GETOBJECT` 핸들러 작성 불요.

## 대안 검토

- **`partial class SettingsDialog` 의 4번째 파일 (`SettingsDialog.Build.cs`) 신설하여 BuildChildren 분리** — Tier-2 라인 카운트 (`<200`) 를 만족시키려면 필요. 하지만 PR-07 §2 의 "partial class 파편화 정리: 3 → 1-2 로 축소 검토" 방향과 모순. **현 PR 에서는 진행 안 함** — 라인 카운트 슬립을 인정하고 §6 에 기록.
- **셸이 `SafeFontHandle` 을 ref-counted 로 외부 노출** — 호출자가 폰트 수명을 명시적으로 관리할 수 있게. 하지만 `using var hFont` 가 모달 루프 + DestroyWindow 구간 전체를 자동으로 덮는 현 구조가 더 안전. **포기**.
- **`useCursorAnchor` 를 `POINT?` 직접 받기** — API 가 더 명시적. 하지만 셸이 어차피 `GetCursorPos` 를 부르므로 호출자가 중복 호출하는 게 비효율. **현 형태(bool 인자, 셸이 cursorPt 캡처) 유지**.

## 회귀 위험

- **위험 1 — CleanupDialog 의 `bringToForeground:true` 가 새로운 사용자 가시 동작 만들 가능성**: 트레이 메뉴 직후 다이얼로그가 명시적으로 포어그라운드를 잡음. ShowWindow 가 어차피 활성화 → 사용자 체감 차이는 거의 없을 것. 운영 중 사용자 보고 대기.
- **위험 2 — `delegate* unmanaged<...>` AOT 호환성**: function pointer 파라미터가 `Func<...>` / `Action<...>` 와 조합돼 셸에 전달. NativeAOT 가 generic delegate 의 closure 와 native function pointer 를 어떻게 처리하는지 — 본 PR 의 AOT publish 가 0 경고 / 4.80 MB clean 통과로 검증.
- **위험 3 — `WS_GROUP` 가 기존 Tab 동작을 깨뜨릴 가능성**: `WS_GROUP` 은 화살표 키 그룹 경계 표시일 뿐 Tab 동작에는 영향 없음. 단, COMBOBOX 의 dropdown 동작이 화살표 키와 충돌하지 않는지 Tier-3 smoke 필요.
- **위험 4 — `borderW = 16` 제거로 contentW 가 미세하게 달라짐**: nonClientW (~16 px @ 96 DPI for default theme) 와 기존 fudge 16 logical px 의 차이가 1-2 px. 자식 컨트롤이 스크롤바를 침범할 위험 — Tier-3 smoke 에서 SettingsDialog 의 38 필드 시각 확인 필요.

## Tier-2 라인 카운트 슬립

PR-07 §3 Tier-2:

- `wc -l App/UI/Dialogs/SettingsDialog.cs` < 200 (현재 440) → **384 (-13%)**, 미달
- `wc -l App/UI/Dialogs/CleanupDialog.cs` < 200 (현재 377) → **325 (-14%)**, 미달
- `wc -l App/UI/Dialogs/ScaleInputDialog.cs` < 150 (현재 274) → **219 (-21%)**, 미달

분석: 잔여 라인의 약 65% 는 `BuildChildren` (자식 컨트롤 생성 + DPI-스케일 변수 선언 + 12개 섹션/60 필드 케이스 분기). 이 영역은 본질적으로 다이얼로그-고유. 추가 감축은 (a) fluent control builder 도입 (별도 모듈 ~100 줄 + 호출 사이트 단순화), 또는 (b) BuildChildren 을 별도 partial 파일로 분리 (`*.Build.cs`) — 둘 다 PR-07 스코프 초과 또는 §2 "partial 축소 검토" 와 모순. **현 PR 은 핵심 가치(셸 추출 + a11y baseline + bringToForeground 통일 fix)에 집중하고 라인 카운트 슬립을 명시적으로 기록**.

## 측정 계획

- PR-07 머지 후 운영 중 CleanupDialog 의 포어그라운드 동작 관찰 — 이상 보고 없으면 fix 유효.
- a11y 변경 — 스크린 리더 사용자 피드백 (현재 KoEnVue 의 a11y 사용자 베이스는 미상, 1차 baseline 만 깔아둠).
- 셸 추출 후 후속 PR 에서 4번째 다이얼로그 추가 시 보일러플레이트 ~50 줄 회피 효과 검증.
