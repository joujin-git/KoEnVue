# PR-30: 커서 이동 딤 — 저속 감지·settle·알파 튜닝

> 상태: **✅ main** — 2026-07-24.
>
> 목적: AlwaysShow 이동 안개가 **저속 이동에서도** 유지되고, **명확히 멈춘 뒤에만** Full(또렷)로 복귀. 이동 중 α도 소폭 상향.
>
> 선행: [PR-29](PR-29-cursor-motion-distraction-reduction.md) 3원 가우시안 안개. 본 PR은 감지·디폴트 파라미터만 — 셰이더/키 스키마 불변.

## 동기

PR-29는 딤/숨김이 **같은** `CursorMotionThresholdPx`(=5)를 썼다. AlwaysPoll≈15ms에서 δ>5는
≈320+ px/s만 “이동”이라, **천천히 움직이면** 틱당 1~4px가 계속 “정지” → 안개가 안 켜지거나
settle 3틱(~45ms)으로 바로 Full 복귀. 사용자: “이동이 **명확히 끝났을 때**만 끝으로 판단 + 이동 중 α↑”.

## 사용자 확정 (2026-07-24)

| # | 항목 | 결정 |
|---|---|---|
| U1 | 딤 임계 | **`CursorMotionDimThresholdPx = 1`** (내부 const, config 키 아님) |
| U2 | 숨김 임계 | **`CursorMotionThresholdPx = 5` 유지** |
| U3 | Settle | **8틱 ≈125ms** |
| U4 | α 디폴트 | **0.22 → 0.30** |
| U5 | enter/exit 물리량 | **동일 맨해튼 δ + exit settle만** (PR-28 B안 금지 유지) |

## 동작

```
AlwaysShow + dim:
  movingDim = (|dx|+|dy|) > CursorMotionDimThresholdPx   // 1
  enter: movingDim → 즉시 딤
  exit:  !movingDim 연속 SettlePolls(=8) → Full

AlwaysShow=false (숨김):
  movingHide = (|dx|+|dy|) > CursorMotionThresholdPx     // 5
```

딤/숨김 **용도별 임계 분리**는 Schmitt(히스테리시스)이지 B안(이질 물리량)이 아님.

## 변경 파일

| 파일 | 변경 |
|------|------|
| `App/Config/DefaultConfig.cs` | `DimThresholdPx=1`, `SettlePolls` 3→8, `Alpha` 0.22→0.30 |
| `App/UI/CursorOverlay.cs` | `movingForDim` / `movingForHide` 분기 |
| `App/UI/CursorMotionDim.cs` | XML 주석 (임계는 호출측) |
| `tests/.../CursorMotionDimTests.cs` | settle=8 · 임계 분리 가드 |
| docs | 본 문서 + INDEX / config-reference / implementation-notes / CHANGELOG |

**불변**: AppConfig 키·Settings UI·Core·셰이더·I18n.

## 검증

- [x] `dotnet build` + `dotnet publish -r win-x64 -c Release` (AOT ~4.82 MB)
- [x] `dotnet test tests\KoEnVue.Tests\KoEnVue.Tests.csproj` — 104/104 PASS
- [ ] Tier-3: 저속 드래그 중 안개 유지 · 완전 정지 후 ~0.1s+ Full · 숨김 모드 회귀 (사용자)

## P1–P6

P3: magic → DefaultConfig. P4: moving 판정 Overlay 1곳. P6: App만.
