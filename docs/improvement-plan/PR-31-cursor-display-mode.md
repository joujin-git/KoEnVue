# PR-31: 커서 표시 3모드 (soft / sharp / motion)

> 상태: **🚧 in progress** — 2026-07-24.
>
> 목적: 커서 인디 선명도를 **이동/정지 동일 옵션 + 이동 중 흐릿하게** 로 확장. 기본값 **항상 흐릿하게**.
>
> 선행: [PR-29](PR-29-cursor-motion-distraction-reduction.md) 안개 · [PR-30](PR-30-cursor-motion-dim-settle-tweak.md) 저속·settle.

## 사용자 확정

| # | 항목 | 결정 |
|---|---|---|
| U1 | 모드 수 | **B안 3모드** |
| U2 | 라벨 | **흐릿하게** / **선명하게** / **이동 중 흐릿하게** |
| U3 | 디폴트 | **soft** (항상 흐릿하게) |
| U4 | 상위 이름 | **커서 인디케이터 표시** |
| U5 | config | `cursor_display_mode` = `soft` \| `sharp` \| `motion` |

## 동작

| 모드 | 런타임 |
|------|--------|
| `soft` | AlwaysShow 시 매 tick 안개 (IME 팝 중에도 Soft 안개 유지 — HighlightScale만) |
| `sharp` | 항상 Full |
| `motion` | PR-29/30 — δ>1 enter, settle 8 exit |

## 마이그레이션

| user JSON | 결과 |
|-----------|------|
| `cursor_display_mode` 있음 | 유지 |
| 구 `cursor_motion_dim_enabled: true` | → `motion` |
| 구 `false` | → `sharp` |
| 둘 다 없음 | → `soft` |

## 변경 요약

- enum `CursorDisplayMode` · `CursorDisplayModeMigration`
- Overlay `AdvanceForMode` · Tray 라디오 서브메뉴 · Settings Combo · I18n
- docs: config-reference / User_Guide / implementation-notes / CHANGELOG / INDEX

## 검증

- [x] `dotnet build` + `dotnet publish -r win-x64 -c Release`
- [x] `dotnet test` — 110/110 PASS
- [ ] Tier-3: soft 정지=흐릿하게 · sharp 이동=선명하게 · motion=기존 · 트레이/Settings (사용자)
