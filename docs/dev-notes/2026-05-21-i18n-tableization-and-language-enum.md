# I18n 테이블화 + `AppLanguage` enum 전환 (PR-06)

**Date**: 2026-05-21
**Scope**: D3 + D4 (improvement-plan 4-라운드 리뷰 발견)
**Status**: 완료 (Tier-1 + Tier-2 통과, Tier-3 사용자 검증 대기)

## 무엇이 바뀌었나

### D3 — `I18n.cs` 41 property 의 삼항 패턴 → Dictionary 테이블

이전:

```csharp
public static string MenuExit => _isKorean ? "종료" : "Exit";
public static string TooltipHangul => _isKorean ? "한글 모드" : "Hangul Mode";
// ... 41개 동일 패턴
```

이후:

```csharp
private enum I18nKey { MenuExit, TooltipHangul, /* ... 46 항목 */ }

private static readonly Dictionary<I18nKey, (string Ko, string En)> _table = new()
{
    [I18nKey.MenuExit]      = ("종료", "Exit"),
    [I18nKey.TooltipHangul] = ("한글 모드", "Hangul Mode"),
    // ...
};

private static string Get(I18nKey key)
{
    var (ko, en) = _table[key];
    return _isKorean ? ko : en;
}

public static string MenuExit      => Get(I18nKey.MenuExit);
public static string TooltipHangul => Get(I18nKey.TooltipHangul);
```

호출자(40+ 사이트의 `I18n.MenuExit` 등) 영향 0 — public surface 동일.

매개변수를 받는 헬퍼 3종(`GetSizeLabel(int)` / `FormatCustomScaleLabel(double)` / `GetTrayTooltip(ImeState)`)은 메서드 형태 유지. 단, locale-dependent 접미사 `"배"` / `"x"` 만 `I18nKey.SizeLabelSuffix` 로 분리해 `_isKorean ?` 분기가 `Get` 한 곳에만 잔존하도록 정리 — Tier-2 guard `_isKorean ? in I18n.cs == 1` 을 satisfy.

### D4 — `string Language` → `enum AppLanguage`

이전:

```csharp
public string Language { get; init; } = "auto";

// I18n.cs
public static void Load(string language) => _isKorean = language switch
{
    "en" => false,
    "ko" => true,
    "auto" => IsSystemKorean(),
    _ => true,
};

// SettingsDialog.Fields.cs — 35 줄의 LanguageToIndex / IndexToLanguage 헬퍼
private static int LanguageToIndex(string lang) => lang switch { "ko" => 1, "en" => 2, _ => 0 };
private static string IndexToLanguage(int i) => i switch { 1 => "ko", 2 => "en", _ => "auto" };
```

이후:

```csharp
public AppLanguage Language { get; init; } = AppLanguage.Auto;

// I18n.cs
public static void Load(AppLanguage language) => _isKorean = language switch
{
    AppLanguage.En => false,
    AppLanguage.Ko => true,
    AppLanguage.Auto => IsSystemKorean(),
    _ => true,
};

// SettingsDialog.Fields.cs — 헬퍼 2종 제거, 다른 11개 enum Combo 와 동일 패턴
c => (int)c.Language,
(c, i) => c with { Language = (AppLanguage)Math.Clamp(i, 0, 2) }
```

`[JsonStringEnumConverter<AppLanguage>] + [JsonStringEnumMemberName("auto"/"ko"/"en")]` 로 디스크 표현은 기존 string 과 비트 단위 동일 — v0.9.x → v0.10.x config.json 그대로 로드.

## 왜

**D3**: 3번째 언어(예: 일본어) 추가 시 현 패턴은 41 줄 동시 수정 + 누락 시 silent 한 항목만 fallback. 테이블 패턴은:
- 신규 키 추가 위치가 자명 (`I18nKey` enum + `_table` dictionary)
- 모든 키가 같은 행에 모든 언어를 갖도록 강제 — 누락은 컴파일 에러 또는 KeyNotFoundException
- 3rd 언어 추가는 튜플 확장 (`(Ko, En, Ja)`) 또는 `Dictionary<(I18nKey, AppLanguage), string>` 으로 차원 추가

**D4**: 3-상태 enum 의미를 string 으로 표현하는 건 P3 위반. 잠재 결함:
- 사용자가 `"language": "korean"` 또는 `"language": "KO"` 같은 오타를 적으면 silent 로 fallback (`_ => true`) — 한국어로 동작하지만 본인 의도는 영문일 수 있음. enum 으로 바뀌면 `Settings.Validate` 의 `EnumOrDefault` 가 `Logger.Warning` 으로 가시화 가능 (현재는 silent Auto 폴백 — 추후 정책 강화 여지).
- 다른 12개 enum 과 일관성 — `DisplayMode` / `Theme` / `DragModifier` 등 모두 `[JsonStringEnumConverter<T>]` 패턴이라 `Language` 만 예외였음.

## 대안

### D3 대안 1: T4 / source generator
컴파일 타임 코드 생성으로 같은 효과. .NET 10 source generator 가능하지만 NativeAOT 트림 정책 + 빌드 시간 트레이드오프 비교 시 Dictionary 가 단순 + 충분. CLAUDE.md P1("Zero external NuGet") 위반은 아니지만 generator infra 자체가 새로운 의존성.

**채택 안 함** — Dictionary 의 런타임 비용 (정적 초기화 1회, lookup O(1)) 이 무시 가능하고 코드 분해성이 더 높다.

### D3 대안 2: `Dictionary<I18nKey, string[]>` (언어 차원 = 배열 인덱스)
3rd 언어 추가 시 튜플 변경 없이 배열에 append. 하지만 컴파일러가 각 항목의 길이를 강제하지 못해 누락 시 런타임 IndexOutOfRange.

**채택 안 함** — 튜플 (`(Ko, En)`) 이 형 안전성 ↑.

### D4 대안 1: enum 이름을 `Language` 로 둠
`AppConfig.Language` property 와 충돌. C# 컴파일러는 `public Language Language { get; init; } = Language.Auto` 를 통과시키지만 (property 이름이 타입을 가리는 건 표준 규칙) 가독성 저하.

**채택 안 함** — PR-06 §4 risk 2 에서 `AppLanguage` 권장.

### D4 대안 2: `Settings.Validate` 에서 `Logger.Warning` 발화
잘못된 enum 값에 대해 silent Auto 폴백 대신 Warning. PR-06 §3 Tier-3 항목에 "잘못된 값 `"language": "fr"` → Validate가 Auto로 fallback + warning" 있음. 하지만 다른 12개 enum 모두 silent 폴백 — Language 만 다르게 처리할 명분 부족.

**보류** — 향후 PR (가능한 PR-09 logging policy) 에서 모든 `EnumOrDefault` 폴백을 일괄 Warning 발화하도록 정책 통일 검토.

## 회귀 위험

### 위험 1: 기존 `config.json` 에 `"language": null` 또는 다른 비정상 값
STJ source-gen 의 `JsonStringEnumConverter<T>` 는 null 을 받으면 JsonException throw (legacy enum default 처리 아님). 단 `AppConfig.Language` 의 init 디폴트가 `AppLanguage.Auto` 이므로 JSON 에 키 자체가 없으면 정상.

**완화**: 기존 `EnsureSubObjects` 의 `Language = config.Language ?? "ko"` fallback 은 enum 전환으로 제거됨. v0.9.x → v0.10.x 마이그레이션 시 `"language": null` 명시 user 가 있다면 JSON 파싱 실패 → `Settings.Load` 의 catch → 기본값 반환 (파일 보존). 실측 빈도 거의 0 (디폴트는 `"auto"` string).

### 위험 2: `Logger.Warning` 의 timing
`Settings.Validate` 가 `Logger.Initialize` 이전에 호출됨 (PR-01 patterns 와 동일). enum 폴백 Warning 은 Trace 만 — 파일 로그 미기록. 단 현 구현은 `EnumOrDefault` 가 silent 라 어차피 Warning 없음.

**완화**: 의도된 동작 유지.

### 위험 3: 3rd-party tool 의 config.json 편집
외부 도구가 `"language": "ko"` / `"en"` / `"auto"` 외 문자열을 적을 가능성 — 예: locale 표준 `"ko-KR"` / `"en-US"`. STJ converter 가 정의 외 string 을 받으면 JsonException → Settings.Load 가 기본값으로 폴백 + 파일 보존 (mtime 갱신으로 폴링 스팸 차단).

**완화**: 사용자 직접 편집 시 README/User_Guide 에 명시 (현재는 config 키 표가 README 에 없어 별도 갱신 불요 — `language` 는 트레이 메뉴와 다이얼로그 UI 에서만 노출).

## 측정 계획

- **N=1 smoke**: 사용자 정상 부팅 + 트레이 메뉴 한국어/영문 표시 + Settings 에서 Auto/Ko/En 전환 즉시 반영 확인. (PR-06 §3 Tier-3)
- **호환성**: 기존 `config.json` 의 `"language": "auto"` 그대로 로드 (Tier-3 ③).
- **잘못된 값**: `"language": "fr"` 로 편집 후 부팅 → 현재 구현은 JSON 파싱 단계에서 JsonException → `Settings.Load` 가 기본값으로 폴백, 파일은 보존 (PR-06 §3 Tier-3 ④ 의 "warning" 부분은 현 구현엔 없음 — 폴백 메커니즘이 다른 layer).
- **장기**: 3rd 언어 추가 요청이 들어오면 본 문서를 절차 매뉴얼로 활용. 튜플 → 3-tuple 확장 또는 `Dictionary<(I18nKey, AppLanguage), string>` 으로 차원 확장.

## 참고 파일

- [App/Localization/I18n.cs](../../App/Localization/I18n.cs)
- [App/Models/AppLanguage.cs](../../App/Models/AppLanguage.cs)
- [App/Models/AppConfig.cs](../../App/Models/AppConfig.cs) — `Language` property
- [App/Config/Settings.cs](../../App/Config/Settings.cs) — `Validate` / `EnsureSubObjects`
- [App/UI/Dialogs/SettingsDialog.Fields.cs](../../App/UI/Dialogs/SettingsDialog.Fields.cs) — 언어 Combo
- [docs/improvement-plan/PR-06-i18n-and-language-enum.md](../improvement-plan/PR-06-i18n-and-language-enum.md) — PR 명세
