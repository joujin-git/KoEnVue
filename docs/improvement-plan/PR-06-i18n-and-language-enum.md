# PR-06: I18n table + Language enum

**Status**: 🚧 in progress (Tier-1+2 통과, Tier-3 사용자 검증 대기)
**Branch**: feat/pr-06-i18n-language
**Base**: main (PR-03 후 권장)
**Risk**: Low
**Estimated session size**: M (1-2시간)

## 1. 목적 (Why)

1. **D3**: [I18n.cs:50-178](../../App/Localization/I18n.cs#L50)의 41개 string property가 모두 `_isKorean ? "ko" : "en"` 삼항. 3번째 언어 추가 시 41개 모두 수정. → 내부적으로 `Dictionary<key, (ko, en)>` 테이블로 변경, public surface는 유지(호출자 영향 0).

2. **D4**: [AppConfig.cs:102](../../App/Models/AppConfig.cs#L102)의 `string Language = "auto"`. 3-상태 enum이지만 string. [I18n.cs:27-33](../../App/Localization/I18n.cs#L27)와 [SettingsDialog.Fields.cs:456-470](../../App/UI/Dialogs/SettingsDialog.Fields.cs#L456) 두 곳에서 string switch. P3 위반. → `enum Language { Auto, Ko, En }` + JsonStringEnumMemberName converter.

## 2. 변경 범위 (What)

### 코드 — D3 I18n 테이블화
- [ ] [App/Localization/I18n.cs](../../App/Localization/I18n.cs) — 41개 property를 다음 패턴으로 변환:
  - 내부 `private static readonly Dictionary<I18nKey, (string Ko, string En)> _table = new() { … }`
  - `private enum I18nKey { MenuOpen, MenuExit, … }` (41 항목)
  - 각 public property: `public static string MenuOpen => Get(I18nKey.MenuOpen);`
  - `private static string Get(I18nKey key) => _isKorean ? _table[key].Ko : _table[key].En;`
- [ ] 호출자(예: `I18n.MenuOpen`)는 변경 불필요

### 코드 — D4 Language enum
- [ ] [App/Models/Language.cs](../../App/Models/) 신규 — `enum Language { Auto, Ko, En }` + `[JsonStringEnumMemberName("auto"/"ko"/"en")]`
- [ ] [App/Models/AppConfig.cs:102](../../App/Models/AppConfig.cs#L102) — `string Language = "auto"` → `Language Language = Language.Auto`
- [ ] [App/Localization/I18n.cs:Load](../../App/Localization/I18n.cs#L25) — 시그너처를 `Load(Language lang)` 또는 `Load(string)` 유지하고 내부 변환
- [ ] [App/UI/Dialogs/SettingsDialog.Fields.cs:456-470](../../App/UI/Dialogs/SettingsDialog.Fields.cs#L456) — `LanguageToIndex`/`IndexToLanguage`를 `(int)lang`/`(Language)i`로 단순화
- [ ] [App/Config/Settings.cs:Validate](../../App/Config/Settings.cs#L143-153)에 `Language = EnumOrDefault(config.Language, Language.Auto)` 항목 추가
- [ ] [App/Models/AppConfig.cs:AppConfigJsonContext](../../App/Models/AppConfig.cs#L193-205)에 `Language` enum 등록 (실은 자동 detect 되지만 확실히)
- [ ] [App/Config/Settings.cs:EnsureSubObjects:579](../../App/Config/Settings.cs#L579) — `Language = config.Language ?? "ko"` 줄 제거 (enum은 non-nullable)

### 문서
- [ ] `CHANGELOG.md` [Unreleased] / Changed에 항목 (config.json `language` 키는 동일 문자열로 호환됨 명시)
- [ ] `docs/architecture.md` I18n 섹션 — 테이블 구조 명시
- [ ] `README.md` 및 `docs/User_Guide.md`의 config 키 표에서 `language` 값 enum화 반영 (단, 기존 string 값 호환)
- [ ] `docs/conventions.md` P3 항목에 "config의 모든 3-상태 이상 키는 enum 권장. string 비교 0건이 목표" 추가
- [ ] `docs/dev-notes/2026-05-21-i18n-tableization-and-language-enum.md` 신규 — 3rd 언어 추가 절차 예시

## 3. 검증 기준 (Done When)

### Tier 1
- [ ] `dotnet build` 통과
- [ ] `dotnet publish -r win-x64 -c Release` 통과
- [ ] invariant 4종 0 매치

### Tier 2 — grep 가드
- [ ] `git grep "_isKorean ?" App/Localization/I18n.cs` 1 매치만 (`Get` 메서드 안)
- [ ] `git grep "I18nKey" App/Localization/I18n.cs` 1+ 매치
- [ ] `git grep "enum Language" App/Models/Language.cs` 1 매치
- [ ] `git grep "string Language" App/Models/AppConfig.cs` 0 매치
- [ ] `git grep '"ko"\|"en"\|"auto"' App/UI/Dialogs/SettingsDialog.Fields.cs` 0 매치 (모두 enum)

### Tier 3 — 수동 smoke
- [ ] 트레이 메뉴/다이얼로그의 한국어/영어 표시 정상
- [ ] SettingsDialog에서 언어 변경 (Auto → Ko → En) → 즉시 반영
- [ ] 기존 `config.json`의 `"language": "auto"` 그대로 로드 (호환 확인)
- [ ] 잘못된 값 `"language": "fr"` → Validate가 Auto로 fallback + warning

## 4. 사이드 이펙트 / 위험

- **위험 1**: I18n 테이블에 enum 41개 신규. enum 이름은 의미적이어야 함 (`MenuOpen`, `MenuExit`, ...). 기존 property 이름과 동일하게 유지 권장 — code review 쉬움.
- **위험 2**: `Language Language = Language.Auto`는 record property와 enum 타입 이름이 동일 → 컴파일러 모호. 대안: enum을 `AppLanguage`로 명명. **결정**: `AppLanguage` 권장.
- **위험 3**: 기존 `config.json`에 `"language": null`이 있는 경우? STJ enum은 null 거부 → JsonException. 단 EnsureSubObjects가 `config.Language ?? "ko"`로 처리했었음. enum 도입 후엔 STJ가 default(`Auto`)로 deserialize. **검증 필요**: STJ가 null을 default로 처리하는지 확인.

## 5. 롤백 절차

- 단순 revert 가능 (Y) — D3는 호출자 영향 0, D4는 AppConfig 스키마 호환
- 데이터 영향 없음 (N)

## 6. 세션 진행 로그

| Date | Session | What | Next |
|---|---|---|---|
| 2026-05-21 | 1 | D3+D4 구현 완료. (D3) `I18n.cs` 41 property → `Dictionary<I18nKey, (Ko, En)>` + `Get(key)` dispatcher. 매개변수 헬퍼 3종(`GetSizeLabel`/`FormatCustomScaleLabel`/`GetTrayTooltip`)은 메서드 유지하되 locale suffix `"배"`/`"x"`만 `SizeLabelSuffix` 키로 분리해 `_isKorean ?`가 `Get` 한 곳에만 잔존. (D4) `AppLanguage { Auto, Ko, En }` enum 신설(`AppConfig.Language` property와 이름 충돌 회피로 `AppLanguage` 채택, risk 2 따름). `AppConfig.Language` `string → AppLanguage`. `I18n.Load(AppLanguage)` 시그너처 변경. `Settings.Validate`에 `Language = EnumOrDefault(config.Language, AppLanguage.Auto)` 추가. `EnsureSubObjects`의 `Language = config.Language ?? "ko"` 제거(enum non-nullable). `SettingsDialog.Fields.cs`의 `LanguageToIndex`/`IndexToLanguage` 헬퍼 2종 삭제 + Combo가 `(int)c.Language` / `(AppLanguage)Math.Clamp(i, 0, 2)`로 단순화(다른 11개 enum과 동일 패턴). Tier-1 debug + AOT publish clean(0 경고, 4.82 MB). Tier-2 grep 가드 5종 통과: `_isKorean ? in I18n.cs = 1`(Get만), `I18nKey in I18n.cs = 97`, `enum AppLanguage = 1`, `string Language in AppConfig = 0`, `"ko"/"en"/"auto" in SettingsDialog.Fields.cs = 0`. invariant 4종 + P5 2종 0 매치. 문서 5건 갱신(CHANGELOG / architecture I18n.cs 항목 + Models enum 리스트 / conventions P3 / dev-notes 신규 / PR-06 §6). README/User_Guide는 `language` 키 참조 0이라 갱신 불요(spec §2 docs에선 "선택" 표기). | Tier-3 수동 smoke (사용자 검증) 후 머지 |
