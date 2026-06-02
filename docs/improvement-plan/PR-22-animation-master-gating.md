# PR-22: animation_enabled 를 진짜 마스터 스위치로 복원 (A안)

**상태**: 구현 (2026-06-02)
**Depends on**: `e253f2f` (slide+highlight 합성 + `SlideAnimation` 기본 ON 전환)

## 동기

트레이 "애니메이션 사용"(config `animation_enabled` / [`AppConfig.AnimationEnabled`](../../App/Models/AppConfig.cs)) 이
코드상 **fade(페이드 인/아웃)만** 게이팅한다 — [`OverlayAnimator`](../../Core/Animation/OverlayAnimator.cs) 의
fade-in / fade-out / dim-idle 3곳에서만 `AnimationEnabled` 를 본다. 그러나 셋 다 **전체 마스터**로 규정:

- **라벨** "애니메이션 사용" — 전체 의미
- **PRD** [KoEnVue_PRD.md:121](../KoEnVue_PRD.md) "트레이 메뉴 '애니메이션 사용' 으로 전체 on/off"
- **config-reference** [config-reference.md:73](../config-reference.md) "`false` 면 fade/highlight/slide 모든 애니메이션 비활성"

→ 의도-구현 불일치. highlight 는 `change_highlight`, slide 는 `slide_animation` 이 독립 게이팅하며,
`e253f2f` 로 `SlideAnimation` 이 기본 ON 으로 전환되면서 **"애니메이션 사용을 꺼도 slide 가 계속
미끄러지는"** 모순이 두드러졌다 (착수 트리거).

문서·PRD·라벨(3 진실원)이 일관되게 "마스터"를 의도하므로 **코드를 그 의도에 맞춘다 (A안)**.
대안 C′(라벨을 "페이드 인/아웃 사용"으로 축소)은 일관된 의도를 코드 일탈에 맞춰 후퇴시키고
PRD/config-reference 다수 하향 수정을 부르므로 기각.

## 설계 결정: 파사드 합성 (Core 무변경)

OverlayAnimator(Core) 내부 가드 3곳에 `AnimationEnabled &&` 를 산개하는 대신, App 파사드
[`Animation.BuildAnimationConfig`](../../App/UI/Animation.cs) **1지점에서 합성**:

```csharp
ChangeHighlight: config.AnimationEnabled && config.ChangeHighlight,
SlideAnimation:  config.AnimationEnabled && config.SlideAnimation,
```

fade 는 이미 OverlayAnimator 가 `AnimationEnabled` 로 직접 게이팅(불변). highlight/slide 는 파사드가
합성한 "최종 플래그"를 엔진이 그대로 소비 → **Core 상태머신 분기 무변경**.

**근거**:
- "마스터" 의미가 App 1지점에 명시 — **P4 단일 진실원**.
- Core 무변경 → `e253f2f` 의 slide+highlight 합성 로직과의 상호작용 회귀 표면 0.
- **P6 정합** — Core 는 `AnimationConfig` 만 소비, App 이 합성. 누출 없음.

## 변경 범위

| 파일 | 변경 |
|------|------|
| [App/UI/Animation.cs](../../App/UI/Animation.cs) | `BuildAnimationConfig` 합성 2줄 + `private`→`internal`(테스트 접근) + XML doc |
| [tests/.../AnimationFacadeTests.cs](../../tests/KoEnVue.Tests/Unit/AnimationFacadeTests.cs) | 신규 — 마스터 게이팅 4 케이스 박제 |
| CHANGELOG.md | Fixed 항목 |
| Core/Animation/OverlayAnimator.cs | **무변경** (파사드안) |
| OverlayAnimatorTests.cs | **무변경** — Core 직접 생성이라 파사드 합성 비경유 |

## 사용자 가시 영향

- "애니메이션 사용" OFF → fade·slide·highlight 전부 즉시 정지 (이전: slide/highlight 잔존).
- 기존 `animation_enabled:false` 사용자 → slide/highlight 도 정지 (그들의 "애니메이션 끄기" 기대 부합).
- config 키 이름·값 불변 → **마이그레이션 불필요**.
- 트레이 메뉴 구조 무변경 (slide 미노출 유지 — [implementation-notes.md](../implementation-notes.md) "사용 빈도 낮아 트레이 미노출" UX 정책).

## P규칙

P1 무관 · P2 라벨 불변 · P3 bool 합성 · **P4 ✅ 마스터 단일 진실원** · P5 무관 · **P6 ✅ Core 무변경**.

## 검증 invariant

```bash
git grep -n "AnimationEnabled && config.ChangeHighlight" App/UI/Animation.cs   # 1
git grep -n "AnimationEnabled && config.SlideAnimation"  App/UI/Animation.cs   # 1
git grep -n "AnimationEnabled" Core/Animation/OverlayAnimator.cs               # fade 3곳만 (불변)
```

## 후속 (실측 확인)

- "애니메이션 사용" OFF → 위치 이동 시 slide 미발생 + IME 전환 시 highlight 미발생 + 표시/숨김 즉시(fade 미발생).
- "애니메이션 사용" ON 회귀 — `e253f2f` slide+highlight 합성 정상(동시 진행 시 인디 안 사라짐).
