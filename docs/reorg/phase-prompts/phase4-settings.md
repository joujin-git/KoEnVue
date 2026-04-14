# Phase 4/5 — Task C: Settings 분리 (C11)

> 이 파일은 **단일 세션에서 붙여 넣어 실행**하기 위한 self-contained 프롬프트다.
> Phase 3가 끝나고 C10 커밋이 있는 상태에서 시작한다.
>
> ⚠ **가장 민감한 Phase다.** Risk 2 (NativeAOT ILC 회귀)가 직접적으로 걸린다. Agent를 돌리기 전에 `docs/reorg/09-risks-and-reuse.md`의 Risk 2 부분을 반드시 먼저 읽는다.

---

## Task

Stage 4 Task C를 수행한다. `App/Config/Settings.cs`의 **재사용 가능한 JSON Load/Save/HotReload 메커니즘**을 제네릭 `Core/Config/JsonSettingsManager<T>`로 추출한다. `AppConfig`-specific 로직(ThemePresets.Apply, MergeProfile, Validate, Migrate, EnsureSubObjects)은 **App/Config에 잔존**. 기존 `Settings.cs` public static API는 시그니처 유지. 커밋 C11을 만든다.

## Context

- 저장소: `e:\dev\KoEnVue`
- 브랜치: `master`
- 직전 상태: Phase 3 C10 완료. Release exe는 Phase 1 기준 ±수 KB.
- 관련 문서:
  - `docs/reorg/05-stage4-core-extraction.md` Task C 섹션 (필독)
  - `docs/reorg/09-risks-and-reuse.md` **Risk 2 (NativeAOT ILC 회귀) — 반드시 정독**
  - `CLAUDE.md` Settings 관련 Key Implementation Decisions (STJ record init defaults, MergeWithDefaults, 삭제 안전 hot-reload, 손상 설정 스팸 방지, 자동 생성)

## Hard constraints

- **P1~P6 유지.** `git grep "KoEnVue\.App" Core/` → 0.
- Core/Config/JsonSettingsManager<T>는 `AppConfig`, `DefaultConfig`, `ThemePresets`, `ImeState`, `AppProfileMatch` 등 App 도메인 타입을 **직접 참조하지 않는다** (Risk 4).
- **Risk 2**: NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false` 조건에서 제네릭 `JsonSerializer.Serialize<T>`는 호출할 수 없다. 반드시 `JsonTypeInfo<T>`를 **생성자에서 주입**받아 `JsonSerializer.Serialize(stream, value, typeInfo)` 형태로만 호출해야 한다.
- `App/Config/Settings.cs` public static API는 **시그니처 변경 금지**.
  - 현 public static 3종: `Load()`, `Save(AppConfig config, string? path = null)`, `CheckConfigFileChange(IntPtr hwndMain)`. `Program.cs`가 참조하는 이 3개 시그니처는 전부 유지한다. 에이전트는 작업 시작 전에 `App/Config/Settings.cs`를 직접 읽어 실제 시그니처를 확인할 것.
- 삭제 안전 hot-reload (`File.Exists` 가드), 손상 설정 스팸 방지(`_lastConfigMtime` 갱신), 첫 실행 시 자동 생성 — 세 가지 동작은 **Core 제네릭 경로에서도 동일하게 유지**한다.
- Release 퍼블리시 exe 크기는 Phase 3 기준 ±수 KB. 크게 변하면 ILC tree-shaking이 망가졌다는 신호.

## 계획 재검토 원칙

**구현 중 계획 재검토가 필요하면 먼저 알려줘.** 에이전트가 `App/Config/Settings.cs`를 실제로 읽어본 결과, 이 프롬프트의 `JsonSettingsManager<T>` 시그니처(생성자 `JsonTypeInfo<T>` 주입, `merge`/`migrate` 콜백 분할), `Load()` 내부 순서(File.Exists → Deserialize → merge → migrate → mtime 갱신), 삭제 안전 hot-reload · 손상 파일 스팸 방지 · 자동 생성 동작의 Core 보존 원칙, `App/Config/Settings.cs` public static 3종(`Load`/`Save`/`CheckConfigFileChange`) 시그니처 유지, Risk 4(AppConfig/DefaultConfig/ThemePresets 누출 금지) 중 하나라도 실제 코드와 중요한 불일치가 있다고 판단되면, **구현을 시작하기 전에** 메인 세션에 보고하고 사용자 판단을 받는다. 특히 **Risk 2(NativeAOT ILC 회귀) 회피를 위해 제네릭 포기 · 비제네릭 전환 · `JsonSerializerContext` 구조 변경 등 대안 설계가 필요하다고 판단되면 절대 독자 판단으로 진행하지 않고** 즉시 중단해 사용자 승인을 받는다. 에이전트의 독자 수정은 ILC 회귀로 직결될 수 있다. 보고 후 승인을 받을 때까지 기존 가정을 그대로 따른다.

## 0. Sanity check

- `git status` clean.
- `git log --oneline -5`에 C10 커밋 존재.
- `git grep "KoEnVue\.App" Core/` → 0.
- `git grep "AppConfig" Core/` → 0.
- `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5).
- `git grep "using KoEnVue\.App" Core/Animation/ Core/Overlay/` → 0 (Phase 2/3 가드 유지).
- `dotnet build` 성공.

## 1. 작업 에이전트 구성

**1개의 general-purpose 에이전트**에게 구현을 위임한다. Risk 2가 직접 걸린 Phase이므로 에이전트 프롬프트에 Risk 2 경고를 반드시 포함한다.

### Agent — Task C 구현

프롬프트 지시:

- 목적: `App/Config/Settings.cs`의 Load/Save/HotReload 뼈대를 `Core/Config/JsonSettingsManager<T>`로 추출하고, `App/Config/Settings.cs`는 `JsonSettingsManager<AppConfig>` 인스턴스 + AppConfig-specific 파이프라인(MergeWithDefaults → Deserialize → EnsureSubObjects → Migrate → Validate → ThemePresets.Apply)으로 감싸는 facade로 리팩터링.
- 필수 읽기:
  - `App/Config/Settings.cs` 전체
  - `App/Config/DefaultConfig.cs`
  - `App/Config/ThemePresets.cs`
  - `App/Models/AppConfig.cs` (특히 `[JsonSerializable]` 선언)
  - `Core/Config/JsonSettingsManager.cs` (Phase 1 skeleton)
  - `docs/reorg/05-stage4-core-extraction.md` Task C
  - `docs/reorg/09-risks-and-reuse.md` Risk 2 (전부)
- **Risk 2 경고 (에이전트 프롬프트에 문자 그대로 포함)**:
  - NativeAOT + `JsonSerializerIsReflectionEnabledByDefault=false` 조건에서는 reflection 기반 제네릭 직렬화가 금지된다.
  - `JsonSerializer.Serialize<T>(stream, value)` 호출은 **절대 금지**. 반드시 `JsonSerializer.Serialize(stream, value, JsonTypeInfo<T>)` 호출만 사용.
  - `JsonTypeInfo<T>`는 `JsonSettingsManager<T>` **생성자에서 주입**받는다.
  - `AppConfig`의 `JsonTypeInfo<AppConfig>`는 기존 `JsonSerializerContext` 소스 제네레이터에서 이미 제공 중이므로 그것을 주입한다.
  - `MergeWithDefaults`가 내부적으로 사용하는 `JsonNode`/`JsonObject` 경로도 전부 `JsonTypeInfo`를 거친 serialize/deserialize로 바꿔야 한다.
- 구현 지침:
  1. `JsonSettingsManager<T>` 시그니처 (제안):

        internal sealed class JsonSettingsManager<T>
            where T : class
        {
            public JsonSettingsManager(
                string filePath,
                JsonTypeInfo<T> typeInfo,    // 반드시 주입. 제네릭 reflection 금지
                Func<T, T> migrate,
                Func<T, T> merge);

            public T Load();                  // 파일 없으면 default 생성 → Save
            public void Save(T value);
            public bool CheckFileChange();    // 폴링용. 삭제-세이프 File.Exists 가드 포함
            public event Action<T>? Reloaded; // 또는 콜백 주입 패턴
        }

     - `merge`, `migrate`는 App에서 주입 — `JsonSettingsManager`는 T-특이 로직을 모른다.
     - `Load()`의 내부 순서:
       1. `File.Exists` → 없으면 default 생성 후 `Save` 호출하고 return.
       2. 있으면 파일 읽기 → `JsonSerializer.Deserialize(stream, typeInfo)`.
       3. `merge(loaded)` → merged.
       4. `migrate(merged)` → migrated.
       5. `_lastMtime`을 성공한 파일의 mtime으로 갱신.
       6. 파일 읽기/파싱 실패 시 **손상된 파일의 mtime으로 `_lastMtime` 갱신**(스팸 방지) + `Logger.Warning` 후 default 반환. **catch 블록에서 Save를 호출하지 않는다** — 사용자의 broken 파일은 보존.
  2. `CheckFileChange`의 내부:
     - `File.Exists(_configFilePath)` → false면 return false (삭제 안전 — 원자적 저장 에디터가 rename으로 바꾸는 사이 일시적으로 파일이 없어지는 순간 false positive 방지).
     - `GetLastWriteTimeUtc` → 캐시값과 비교.
     - 변했으면 `Reloaded` 이벤트 발화 (또는 콜백 호출).
  3. `App/Config/Settings.cs` 리팩터링:
     - 정적 필드 `_manager = new JsonSettingsManager<AppConfig>(path, AppConfigJsonContext.Default.AppConfig, migrate, merge)` 보유.
     - 기존 static 메서드는 `_manager.*`로 위임.
     - `merge` 콜백은 기존 `MergeWithDefaults + EnsureSubObjects` 로직을 호출.
     - `migrate` 콜백은 기존 `Migrate + Validate + ThemePresets.Apply` 로직을 호출.
     - `HandleReloaded` 콜백에서 기존 `WM_CONFIG_CHANGED` 포스트 경로로 연결.
     - `_lastConfigMtime`은 Core 제네릭으로 이동, Settings.cs는 더 이상 mtime을 직접 관리하지 않음.
  4. **Risk 4 준수**: Core/Config에 `using KoEnVue.App.*` 금지. `AppConfig`, `DefaultConfig`, `ThemePresets`, `AppProfileMatch`, `ImeState` 이름 금지.
  5. **Risk 5 재확인**: `App/Config/Settings.cs`가 이번 리팩터링에서도 `App/Detector/`를 역참조하지 않아야 한다. `using KoEnVue.App.Detector` 금지. `WindowProcessInfo`는 `Core/Windowing`에 있으므로 그것만 사용.
  6. `Program.cs`와 모든 외부 호출자는 수정 없음.
- 보고 형식: ≤400단어 요약 + 파일 목록 + Risk 2 준수 증명(어떤 호출이 JsonTypeInfo를 거치는지) + 의사결정 포인트.

## 2. 본 세션 작업 — 에이전트 결과 검토

1. `Read`로 변경 파일(`App/Config/Settings.cs`, `Core/Config/JsonSettingsManager.cs`, 필요 시 `App/Models/AppConfig.cs`의 JsonContext 부분) 전체 일독.
2. **Risk 2 수동 검증**:
   - `git grep "JsonSerializer\.Serialize" App/Config/ Core/Config/` 결과에서 모든 호출이 `typeInfo` 파라미터를 받는지 확인.
   - `git grep "JsonSerializer\.Deserialize" App/Config/ Core/Config/`도 동일하게 확인.
   - reflection 기반 호출이 하나라도 있으면 즉시 에이전트에게 수정 지시.
3. `git grep "KoEnVue\.App" Core/` → 0.
4. `git grep "using KoEnVue\.App" Core/Config/` → 0.
5. `git grep "AppConfig\|DefaultConfig\|ThemePresets\|ImeState\|AppProfileMatch" Core/Config/` → 0.
6. `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5 재확인).
7. `git diff --name-only`에서 `Program.cs`, `App/Models/AppConfig.cs`(JsonContext 부분은 허용), 기타 App 파일이 **불필요하게 수정되지 않았는지** 확인.

## 3. 빌드·스모크 (이 Phase의 가장 중요한 단계)

- `dotnet build` 성공.
- `dotnet publish -r win-x64 -c Release` 성공.
  - exe 크기 수치 확인. Phase 3 대비 **±100KB** 내외여야 한다. 그 이상 증가하면 ILC가 제네릭 reflection fallback을 include했다는 강력한 신호 → **즉시 롤백 후 재설계**.
- Release exe 실행 후 다음 매트릭스 전부 확인:
  1. **부팅 + 자동 생성**: `config.json`을 사전에 삭제해 둔 뒤 실행 → 파일이 새로 생성되고 기본값으로 동작하는지.
  2. **정상 로드**: 기존 `config.json`으로 부팅 → 색상/폰트/크기가 파일 값대로 적용되는지.
  3. **Hot-reload 정상 경로**: 실행 중 `config.json`의 `hangul_bg`를 다른 색으로 바꾸고 저장 → 인디케이터 색이 변하는지.
  4. **삭제 안전 hot-reload**: 실행 중 `config.json`을 삭제 → `Load` 스팸이 발생하지 않아야 함. 이 시나리오로 인한 WARN은 **0건**이어야 한다. 1건이라도 새로 찍히면 `CheckConfigFileChange`의 `File.Exists` 가드가 망가진 것이므로 즉시 실패 처리.
  5. **손상 파일**: `config.json` 앞부분에 `트` 같은 잘못된 문자를 삽입 → 첫 감지 시 WARN 1건 후 스팸 없음. 프로세스는 계속 동작.
  6. **손상 → 복구**: 위 상태에서 손상 파일을 올바른 JSON으로 덮어쓰기 → 새 설정이 로드되는지.
  7. **LogLevel hot-reload**: `log_level`을 DEBUG로 바꿔 저장 → 다음 IME 변경 시 DEBUG 로그가 찍히는지.

## 4. 버그 시 대응

- Risk 2 관련 오류는 **재시도 없이 즉시 롤백** (`git restore .`) 후 에이전트 프롬프트에 Risk 2 경고를 더 강하게 넣어 재작성.
- 빌드 실패는 1~2회 에이전트 재시도 후 여전히 실패면 롤백.

## 5. 검증

모두 통과해야 커밋. 실패 시 `git restore .`로 폐기 후 재시도.

1. `git grep "KoEnVue\.App" Core/` → 0.
2. `git grep "using KoEnVue\.App" Core/Config/` → 0.
3. `git grep "AppConfig\|DefaultConfig\|ThemePresets\|ImeState\|AppProfileMatch" Core/Config/` → 0.
4. `git grep "using KoEnVue\.App\.Detector" App/Config/` → 0 (Risk 5).
5. `git grep "JsonSerializer\.Serialize" Core/Config/`에서 모든 호출이 `typeInfo` 인자를 받는다.
6. `dotnet build` / `dotnet publish -r win-x64 -c Release` 성공. exe 크기 변화 +100KB 미만.
7. 스모크 매트릭스 (3번 섹션) 7개 케이스 전부 통과.

## 6. 커밋

- 커밋 메시지: `refactor(reorg): Stage 4 Task C — JSON load/save → Core/Config/JsonSettingsManager<T> (C11)`
- 본문:
  - JsonSettingsManager<T> + JsonTypeInfo 주입 방식 요약
  - Risk 2 준수 증명 (모든 serialize 호출이 JsonTypeInfo 경유)
  - Risk 4/5 준수 grep 결과
  - 삭제 안전 hot-reload / 손상 파일 스팸 방지 / 자동 생성 모두 동작 확인
  - exe 크기 수치

## 7. Phase 종료 시 사용자에게 보고

- ≤200단어.
- 분리된 파일 목록.
- Risk 2 증명 (grep + exe 크기).
- 스모크 매트릭스 7개 결과.
- 다음 Phase에서 Task D(Tray) + 문서 일괄 현행화를 수행한다는 안내.
