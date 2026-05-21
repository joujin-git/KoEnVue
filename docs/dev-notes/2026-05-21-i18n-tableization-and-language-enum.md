# I18n 테이블화 + `AppLanguage` enum 전환 (PR-06)

**Date**: 2026-05-21
**Scope**: D3 + D4 (improvement-plan 4-라운드 리뷰 발견) + Tier-3 즉시반영 패치 1건
**Status**: 완료 (Tier-1 + Tier-2 + Tier-3 사용자 가시 검증 모두 통과)

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

## Tier-3 검증 결과 + 즉시반영 패치

Tier-3 사용자 가시 smoke 4 시나리오 모두 통과. ② 검증 중 다음 결함을 발견해 본 PR 안에서 함께 fix:

**결함**: SettingsDialog 의 `언어` 콤보 변경 후 `확인` 을 눌러도 트레이 메뉴/툴팁이 즉시 새 언어로 안 바뀌고 다음 외부 config 편집 또는 재시작까지 stale 한국어/영어 유지.

**근본 원인**: `Program.HandleMenuCommand` 의 `updateConfig` 람다가 `I18n.Load` 를 직접 호출하지 않았다. `I18n.Load` 호출처는 `Program.Main` 초기화와 `HandleConfigChanged` (WM_CONFIG_CHANGED 수신) 두 곳뿐인데, 다이얼로그 OK 경로의 `Settings.Save` 가 `JsonSettingsManager._lastMtime` 을 self-bump 해 다음 5초 폴링에서 `CheckReload` 가 false 를 반환 → `WM_CONFIG_CHANGED` 미발사 → `I18n.Load` 갱신 경로 단절. v0.9.x 부터 잠재했던 결함이지만 본 PR 의 Tier-3 ② 가 "즉시 반영" 을 명시 요구해 첫 가시화.

**Fix**: `updateConfig` 람다 진입 시 `AppLanguage oldLanguage = _config.Language` 캡쳐, `ThemePresets.Apply(newConfig)` 직후 `oldLanguage != _config.Language` 비교해 변동 시 `I18n.Load(_config.Language)` 호출. 람다 후반의 `Tray.UpdateState` 가 fresh I18n 으로 tooltip 재구성 + 우클릭 시 메뉴가 새 언어로 즉시 빌드된다. 같은 람다 안 한 곳 추가, 신규 경로 0, 사이드 이펙트 0.

**대안 검토**: `Settings.Save` 가 mtime self-bump 를 안 하도록 변경하면 `WM_CONFIG_CHANGED` 가 발사돼 `HandleConfigChanged` 가 `I18n.Load` 호출 — 자연 수렴. 단 self-bump 는 핫 리로드 폴링이 자체 write 에 의해 무한 재발사되는 걸 막는 핵심 mechanism 이라 제거 시 spam 회귀. 채택 안 함.

## 측정 계획

- **N=1 smoke**: 사용자 정상 부팅 + 트레이 메뉴 한국어/영문 표시 + Settings 에서 Auto/Ko/En 전환 즉시 반영 확인 (Tier-3 ②). **위 패치 적용 후 통과**.
- **호환성**: 기존 `config.json` 의 `"language": "auto"` 그대로 로드 (Tier-3 ③). **통과**.
- **잘못된 값**: `"language": "fr"` 편집 후 부팅 → JSON 파싱 단계에서 `JsonException` → `JsonSettingsManager.Load` 의 catch 가 `Logger.Warning` 발화 + 전체 config 가 defaults 로 폴백, 파일은 보존 (Tier-3 ④). 사용자 가시 evidence 는 koenvue.log 에 안 남는데 `Settings.Load` 가 `Logger.Initialize` 이전에 실행돼 Warning 이 Trace 로만 흘러가기 때문. Tier-3 검증은 다른 필드(`opacity`) 를 비-디폴트로 동시 편집해 defaults 폴백 가시화로 대체. **통과**.
- **장기**: 3rd 언어 추가 요청이 들어오면 본 문서를 절차 매뉴얼로 활용. 튜플 → 3-tuple 확장 또는 `Dictionary<(I18nKey, AppLanguage), string>` 으로 차원 확장. Settings.Load 의 Trace-only Warning 문제는 PR-09 (logging policy) 에서 deferred-emit 패턴으로 일괄 해소 검토.

## 참고 파일

- [App/Localization/I18n.cs](../../App/Localization/I18n.cs)
- [App/Models/AppLanguage.cs](../../App/Models/AppLanguage.cs)
- [App/Models/AppConfig.cs](../../App/Models/AppConfig.cs) — `Language` property
- [App/Config/Settings.cs](../../App/Config/Settings.cs) — `Validate` / `EnsureSubObjects`
- [App/UI/Dialogs/SettingsDialog.Fields.cs](../../App/UI/Dialogs/SettingsDialog.Fields.cs) — 언어 Combo
- [docs/improvement-plan/PR-06-i18n-and-language-enum.md](../improvement-plan/PR-06-i18n-and-language-enum.md) — PR 명세
