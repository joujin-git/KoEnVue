using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KoEnVue.Core.Logging;

namespace KoEnVue.Core.Config;

/// <summary>
/// 제네릭 JSON 설정 관리자. 다음 파이프라인을 T-독립적으로 수행한다:
/// <para>
/// MergeWithDefaults → Deserialize → ApplyNullSafetyNet →
/// PostDeserializeFixup → Migrate → Validate → ApplyTheme.
/// </para>
/// <para>
/// NativeAOT 요건으로 <c>System.Text.Json</c> 소스 생성기가 만든
/// <see cref="JsonTypeInfo{T}"/> 를 생성자에 주입받아야 한다. 본 클래스는
/// 리플렉션 경로를 절대 사용하지 않는다 — 모든 직렬화/역직렬화는
/// 주입된 <see cref="JsonTypeInfo{T}"/> 를 경유한다.
/// </para>
/// <para>
/// 파이프라인의 T-종속 단계는 5개의 <c>protected virtual</c> 훅으로 노출되며,
/// App 측 하위 클래스(<c>AppSettingsManager</c> 등)가 필요한 훅만 override 한다.
/// 기본 구현은 전부 identity (인자 그대로 반환).
/// </para>
/// <para>
/// 핫 리로드 감시(<see cref="CheckReload"/>)는 mtime 폴링을 담당하고,
/// 메시지 디스패치는 호출자(App 레이어)가 결정한다 — Core 가 Win32
/// <c>PostMessage</c> 를 알 필요가 없도록.
/// </para>
/// </summary>
internal class JsonSettingsManager<T>
    where T : class, new()
{
    private readonly string _filePath;
    private readonly JsonTypeInfo<T> _typeInfo;

    // _lastMtime 은 메인 스레드(Save/Load)와 감지 스레드(CheckReload)가 공유한다. DateTime 은
    // 64비트 값이라 volatile 키워드를 받지 못하므로 lock 으로 read/write 원자성·가시성을 보장한다.
    //
    // **파일 쓰기와 mtime 조회까지 이 lock 안에서 수행한다** (AUDIT-2026-07-30 §F). 이전에는
    // "I/O 는 lock 밖" 이라 Save 의 쓰기와 mtime 갱신 사이에 틈이 있었고, 그 틈에 감지 스레드가
    // CheckReload 를 돌면 새 mtime ≠ 옛 _lastMtime 이라 **자기 저장을 외부 편집으로 오인**해
    // 불필요한 전체 리로드를 유발했다(배지 깜빡임 + 메모리 전용 변경 소실). 그 리로드가 §B 의
    // WM_CONFIG_CHANGED 재진입 트리거를 늘리는 것이 더 큰 문제였다.
    //
    // 경합 비용은 무시할 수준이다 — CheckReload 는 5초에 1회, Save 는 사용자 조작 시에만 돈다.
    private readonly object _mtimeLock = new();
    private DateTime _lastMtime = DateTime.MinValue;

    /// <summary>
    /// 앱이 <b>마지막으로 디스크와 동기화된 시점</b>의 설정을 직렬화한 것. <see cref="TryLoad"/> 성공과
    /// <see cref="Save"/> 성공이 갱신한다.
    ///
    /// <para>
    /// <see cref="Save"/> 의 3-way 병합 기준선이다 (AUDIT-2026-07-30 §N-48). 저장은 메모리 인스턴스를
    /// 통째로 직렬화해 파일을 덮으므로, 그 사이 사용자가 <c>config.json</c> 에 넣은 편집이
    /// <b>앱이 손대지도 않은 필드까지</b> 사라졌다 — 5초 폴링이 그 편집을 읽어가기 전에 트레이 토글 한
    /// 번이면 충분하다. 이 기준선이 있으면 "앱이 이번에 바꾼 필드" 를 알 수 있고, 나머지는 디스크
    /// 값을 살릴 수 있다.
    /// </para>
    /// </summary>
    private string? _lastPersistedJson;

    public JsonSettingsManager(string filePath, JsonTypeInfo<T> typeInfo)
    {
        _filePath = filePath;
        _typeInfo = typeInfo;
    }

    /// <summary>현재 대상 파일 절대 경로.</summary>
    public string FilePath => _filePath;

    // ================================================================
    // Load — 12 단계 파이프라인
    // ================================================================

    /// <summary>
    /// 대상 파일을 로드.
    /// <list type="bullet">
    ///   <item>파일 없음 → <c>new T()</c> 를 즉시 디스크에 Save 후 반환</item>
    ///   <item>파일 존재 + 정상 → 전체 파이프라인 적용</item>
    ///   <item>파일 존재 + 파싱 실패 → 사용자 파일 덮어쓰지 않음. mtime 갱신으로 폴링 스팸 차단. <c>new T()</c> 인메모리 반환</item>
    /// </list>
    /// </summary>
    public T Load()
    {
        TryLoad(out T value);
        return value;
    }

    /// <summary>
    /// <see cref="Load"/> 와 같은 파이프라인이되 <b>성공 여부를 호출자에게 알린다.</b>
    /// 파싱 실패 시 <c>false</c> 를 반환하며, 이때 <paramref name="value"/> 는 전 필드 디폴트다.
    ///
    /// <para>
    /// 반환값을 무시하고 디폴트를 그대로 채택하면 안 된다 — 핫리로드 호출자(<c>HandleConfigChanged</c>)가
    /// 그렇게 대입하면 이후 <b>어떤</b> 저장 경로(트레이 토글·드래그 종료)든 그 디폴트를 디스크에 확정해
    /// 사용자 설정이 전멸한다. config.json 은 편집 중 한순간만 파싱 불가여도 이 경로를 탄다
    /// (AUDIT-2026-07-30 §G). 부팅 경로처럼 "비교할 기존 인스턴스가 없는" 호출자만 <see cref="Load"/> 로
    /// 디폴트를 받아도 된다.
    /// </para>
    /// </summary>
    /// <param name="value">성공 시 로드된 값, 실패 시 전 필드 디폴트 (항상 non-null).</param>
    /// <returns>파일이 정상 로드됐거나 애초에 없어서 디폴트를 생성한 경우 true, 파싱 실패면 false.</returns>
    public bool TryLoad(out T value)
    {
        if (File.Exists(_filePath))
        {
            try
            {
                string userJson = JsonSettingsFile.ReadAllTextStripBom(_filePath);
                string mergedJson = MergeWithDefaults(userJson, _typeInfo);

                T? config = JsonSerializer.Deserialize(mergedJson, _typeInfo);
                if (config is null)
                {
                    // 유효 JSON 이지만 리터럴 null — 파싱 실패와 같은 등급으로 다룬다.
                    LogProvider.Sink?.Warning($"Config at {_filePath} deserialized to null. Keeping previous settings.");
                    value = new T();
                    return false;
                }

                config = ApplyNullSafetyNet(config);
                config = PostDeserializeFixup(config, mergedJson);
                config = Migrate(config);
                config = Validate(config);
                config = ApplyTheme(config);

                lock (_mtimeLock)
                {
                    _lastMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
                    // 방금 디스크와 동기화됐다 — 다음 Save 의 3-way 병합 기준선을 여기서 세운다 (§N-48).
                    // 파일 원문이 아니라 **앱의 표현**으로 저장해야 주석·포맷 차이가 diff 를 오염시키지 않는다.
                    _lastPersistedJson = JsonSerializer.Serialize(config, _typeInfo);
                }
                LogProvider.Sink?.Info($"Config loaded from {_filePath}");
                value = config;
                return true;
            }
            catch (Exception ex) when (IsExpectedLoadException(ex))
            {
                // 파일은 존재하지만 파싱 실패 — 사용자 복구 가능성을 위해 덮어쓰지 않음.
                // mtime은 갱신해서 5초 폴링이 WM_CONFIG_CHANGED를 무한 재발송하는 스팸을 차단.
                // 정책 항목 1(타입 좁히기): I/O + JSON + 소스젠 비지원 타입만 잡고, 훅(Validate/Migrate/...)
                // 의 로직 버그(NullRef 등)는 propagate 시켜 표면화하도록 한다.
                LogProvider.Sink?.Warning($"Failed to load config from {_filePath}: {ex.Message}. Using defaults without overwriting.");
                try
                {
                    lock (_mtimeLock) _lastMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
                }
                catch (Exception innerEx) when (IsExpectedIoException(innerEx)) { }
                value = new T();
                return false;
            }
        }

        LogProvider.Sink?.Info($"Config not found, creating defaults at {_filePath}");
        T defaults = new();
        Save(defaults);
        value = defaults;
        return true;
    }

    // ================================================================
    // Save — NF-25: 저장 실패 시 인메모리 유지
    // ================================================================

    /// <summary>
    /// 현재 경로로 저장. 실패 시 경고 로그만 남기고 예외를 삼킨다(인메모리 유지).
    /// 성공 시 mtime 을 갱신해 자체 호출에 의한 핫 리로드 루프를 차단.
    ///
    /// <para>
    /// <b>반환값은 실제로 디스크에 확정된 설정이다</b> — 3-way 병합이 일어났으면 <paramref name="value"/>
    /// 와 다르다. 호출자는 이 값을 인메모리에 반영해야 한다. 그러지 않으면 병합 결과가 메모리에
    /// 없는 채로 <c>_lastMtime</c> 이 self-bump 되어 핫리로드도 돌지 않고, <b>다음 저장이 옛 인메모리
    /// 값으로 사용자 편집을 다시 덮는다</b> — 병합이 손실을 한 번만 지연시키는 셈이 된다
    /// (릴리즈 리뷰 2026-08-01 확정 #1·#3·#5, 대조군으로 실측 재현됨).
    /// </para>
    /// </summary>
    /// <returns>디스크에 확정된 설정. 병합이 없었으면 <paramref name="value"/> 그대로.</returns>
    public T Save(T value)
    {
        try
        {
            // 직렬화는 순수 계산이라 lock 밖. 쓰기와 mtime 갱신은 한 덩어리여야 한다 —
            // 그 사이가 열려 있으면 감지 스레드가 자기 저장을 외부 편집으로 오인한다 (§F).
            string rawJson = JsonSerializer.Serialize(value, _typeInfo);
            bool didMerge;

            lock (_mtimeLock)
            {
                // 디스크가 우리가 마지막으로 본 상태에서 벗어났으면, **이번에 바꾼 필드만** 그 위에
                // 얹는다 (§N-48). 그러지 않으면 사용자가 방금 파일에 넣은 편집이 앱이 손대지도 않은
                // 필드까지 통째로 되돌아간다.
                string merged = MergeOntoDiskIfChanged(rawJson, out didMerge);
                string json = FormatJson(merged);

                JsonSettingsFile.WriteAllText(_filePath, json);
                _lastMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
                // 기준선은 **실제로 쓴 내용**이어야 다음 저장의 diff 가 정확하다.
                _lastPersistedJson = merged;
            }
            LogProvider.Sink?.Debug($"Config saved to {_filePath}");

            // 병합이 있었으면 디스크와 메모리가 갈라진 상태다. 방금 쓴 파일을 다시 읽어 호출자에게
            // 돌려줘야 인메모리가 따라잡는다 — TryLoad 가 _lastMtime·_lastPersistedJson 도 정규
            // 표현으로 다시 세우므로 다음 저장의 diff 기준선까지 한 번에 정리된다.
            if (didMerge && TryLoad(out T reloaded))
                return reloaded;
        }
        catch (Exception ex) when (IsExpectedSaveException(ex))
        {
            // NF-25: 저장 실패 시 로그 경고 + 인메모리 유지.
            // 정책 항목 1(타입 좁히기): 로직 버그(NullRef 등)는 전파되어 표면화되도록 한다.
            LogProvider.Sink?.Warning($"Failed to save config: {ex.Message}");
        }

        return value;
    }

    // ================================================================
    // Save 3-way 병합 (AUDIT-2026-07-30 §N-48)
    // ================================================================

    /// <summary>
    /// 디스크가 우리가 마지막으로 본 상태에서 바뀌었으면, <paramref name="nextJson"/> 중
    /// <b>이번에 실제로 바뀐 키만</b> 디스크 내용 위에 얹은 JSON 을 돌려준다. 바뀌지 않았으면
    /// <paramref name="nextJson"/> 그대로.
    ///
    /// <para>
    /// 병합 규칙은 하나다 — <b>앱이 이번에 바꾼 필드는 앱이 이기고, 나머지는 디스크가 이긴다.</b>
    /// 기준선(<see cref="_lastPersistedJson"/>)이 "앱이 마지막으로 디스크와 같다고 아는 상태" 이므로,
    /// 기준선과 <paramref name="nextJson"/> 의 차이가 곧 앱의 의도이고, 그 외 디스크와 기준선의
    /// 차이는 사용자가 파일에 직접 넣은 편집이다.
    /// </para>
    ///
    /// <para>
    /// 병합할 수 없는 상황(기준선 없음 · 디스크 읽기/파싱 실패 · 최상위가 객체가 아님)에서는
    /// <paramref name="nextJson"/> 을 그대로 쓴다. 저장 자체를 포기하면 사용자의 트레이 조작이 조용히
    /// 무시되므로, 그쪽이 더 나쁘다.
    /// </para>
    /// </summary>
    /// <remarks><see cref="_mtimeLock"/> 을 보유한 상태에서 호출한다.</remarks>
    /// <param name="didMerge">실제로 병합이 수행됐으면 true — 호출자가 인메모리를 재동기화해야 한다.</param>
    private string MergeOntoDiskIfChanged(string nextJson, out bool didMerge)
    {
        didMerge = false;
        if (_lastPersistedJson is null) return nextJson;

        DateTime diskMtime;
        try
        {
            if (!File.Exists(_filePath)) return nextJson;
            diskMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            return nextJson;
        }

        // 우리가 마지막으로 본 그대로면 덮어써도 잃을 것이 없다 — 흔한 경로라 파싱 비용을 아낀다.
        if (diskMtime == _lastMtime) return nextJson;

        try
        {
            string diskJson = JsonSettingsFile.ReadAllTextStripBom(_filePath);

            using var baseDoc = JsonDocument.Parse(_lastPersistedJson);
            using var nextDoc = JsonDocument.Parse(nextJson);
            using var diskDoc = JsonDocument.Parse(diskJson, UserJsonDocOptions);

            if (baseDoc.RootElement.ValueKind != JsonValueKind.Object
                || nextDoc.RootElement.ValueKind != JsonValueKind.Object
                || diskDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return nextJson;
            }

            using var ms = new MemoryStream();
            // **Indented 필수** — 앱 직렬화기가 WriteIndented=true 라, 여기서 압축본을 만들면
            // (1) 사용자가 손으로 편집하는 config.json 이 한 줄로 붕괴하고 (2) 그 압축본이
            // _lastPersistedJson 기준선이 되어 다음 SameJson 비교가 전부 어긋난다
            // (릴리즈 리뷰 2026-08-01 확정 #2·#6, 실측 재현).
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                MergeChangedOntoDisk(writer, baseDoc.RootElement, nextDoc.RootElement, diskDoc.RootElement);
            }

            LogProvider.Sink?.Info(
                $"Config changed on disk since last sync; merged only the fields this save touched ({_filePath})");
            didMerge = true;
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex) when (IsExpectedLoadException(ex))
        {
            // 디스크가 깨져 있으면 병합할 대상이 없다 — 앱 값을 그대로 쓴다.
            LogProvider.Sink?.Warning(
                $"Could not merge onto on-disk config ({ex.Message}); writing in-memory settings as-is");
            return nextJson;
        }
    }

    /// <summary>
    /// 3-way 병합의 재귀 본체. <paramref name="baseObj"/> 대비 <paramref name="nextObj"/> 에서 값이
    /// 달라진 키만 <paramref name="nextObj"/> 값을 쓰고, 그 외에는 <paramref name="diskObj"/> 값을 쓴다.
    /// 디스크에만 있는 키(사용자가 손으로 추가한 프로필 항목 등)는 그대로 보존한다.
    /// </summary>
    private static void MergeChangedOntoDisk(
        Utf8JsonWriter writer, JsonElement baseObj, JsonElement nextObj, JsonElement diskObj)
    {
        writer.WriteStartObject();

        foreach (JsonProperty nextProp in nextObj.EnumerateObject())
        {
            writer.WritePropertyName(nextProp.Name);

            bool inBase = baseObj.TryGetProperty(nextProp.Name, out JsonElement baseVal);
            bool inDisk = diskObj.TryGetProperty(nextProp.Name, out JsonElement diskVal);

            // **중첩 객체는 변경 여부와 무관하게 먼저 재귀한다.** 여기서 "앱이 바꿨나" 를 객체 단위로
            // 판정해 서브트리를 통째로 앱 값으로 쓰면, 앱이 그 안의 필드 하나만 바꿔도 사용자가 고친
            // **형제 키가 함께 사라진다** — 예: 사용자가 advanced.overlay_class_name 을 고친 뒤
            // 상세 설정에서 force_topmost_interval_ms 를 바꾸면 advanced 전체가 앱 값으로 덮인다
            // (릴리즈 리뷰 2026-08-01 확정 #12). 스칼라 단위까지 내려가야 규칙이 실제로 성립한다.
            if (inBase && inDisk
                && baseVal.ValueKind == JsonValueKind.Object
                && nextProp.Value.ValueKind == JsonValueKind.Object
                && diskVal.ValueKind == JsonValueKind.Object)
            {
                MergeChangedOntoDisk(writer, baseVal, nextProp.Value, diskVal);
                continue;
            }

            // 앱이 이번에 바꾼 필드 → 앱 값이 이긴다. (기준선에 없던 키도 앱이 새로 만든 것으로 본다.)
            // 디스크에 키가 없는 경우도 앱 값 — 사용자가 지운 키라도 스키마상 필요하다.
            if (!inBase || !inDisk || !SameJson(baseVal, nextProp.Value))
            {
                nextProp.Value.WriteTo(writer);
                continue;
            }

            // 앱이 안 건드린 필드 → 디스크가 이긴다.
            diskVal.WriteTo(writer);
        }

        // 디스크에만 있는 키 보존 — 스키마 밖 항목(미래 키·사용자 메모)을 저장이 삼키지 않도록.
        foreach (JsonProperty diskProp in diskObj.EnumerateObject())
        {
            if (!nextObj.TryGetProperty(diskProp.Name, out _))
                diskProp.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// 두 JSON 값이 <b>의미상</b> 같은지. 원문 텍스트 비교로는 안 된다 — 병합이 한 번이라도 일어나면
    /// 기준선에 <b>디스크 원문 조각</b>이 섞여(<c>diskVal.WriteTo</c> 와 disk-only 키 보존 경로가
    /// raw text 를 그대로 쓴다) 사용자가 손으로 쓴 표기(<c>1.0</c>·<c>1e2</c>·<c>0.80</c>)가 남는데,
    /// 다음 저장의 값은 직렬화기의 정규 표기(<c>1</c>·<c>100</c>·<c>0.8</c>)라 <b>앱이 건드리지도 않은
    /// 필드가 "앱이 바꿈" 으로 오분류</b>돼 디스크 값을 이긴다 (릴리즈 리뷰 2026-08-01 확정 #15).
    ///
    /// <para>
    /// 숫자는 값으로, 문자열·불리언·null 은 타입별로 비교한다. 객체·배열은 공백을 제거한 정규 표기로
    /// 비교하는데 — <b>알려진 한계</b>: 그 안쪽 숫자 표기 차이(<c>[1.0]</c> vs <c>[1]</c>)는 여전히
    /// 다르게 본다. 그 경우 보수적으로 "앱이 바꿈" 이 되어 앱 값이 이기므로 손실 방향이 아니라
    /// 표기 정규화 방향이고, 이 프로젝트의 배열 필드는 앱이 통째로 관리한다.
    /// </para>
    /// </summary>
    private static bool SameJson(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;

        return a.ValueKind switch
        {
            JsonValueKind.Number => a.TryGetDouble(out double da)
                && b.TryGetDouble(out double db)
                && da.Equals(db),
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(Compact(a), Compact(b), StringComparison.Ordinal),
        };
    }

    /// <summary>공백·줄바꿈을 제거한 정규 표기. 포맷 차이가 값 비교를 오염시키지 않도록.</summary>
    private static string Compact(JsonElement element)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            element.WriteTo(writer);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ================================================================
    // CheckReload — mtime 폴링
    // ================================================================

    /// <summary>
    /// mtime 변경 시 true 반환. Load() 재실행 여부는 호출자 책임.
    /// <para>
    /// 삭제된 파일은 <see cref="File.GetLastWriteTimeUtc"/> 가 1601-01-01 센티널을
    /// 반환해 "변경됨"으로 오인되고 뒤따르는 <see cref="Load"/> 가 기본값으로 리셋해버린다.
    /// 존재 확인으로 이 체인을 차단.
    /// </para>
    /// </summary>
    public bool CheckReload()
    {
        if (!File.Exists(_filePath)) return false;

        try
        {
            // 조회까지 lock 안 — Save 의 "쓰기 → mtime 갱신" 사이를 관찰하면 자기 저장을
            // 외부 편집으로 오인한다 (§F).
            lock (_mtimeLock)
            {
                DateTime mtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
                if (mtime != _lastMtime)
                {
                    _lastMtime = mtime;
                    return true;
                }
            }
        }
        catch (Exception ex) when (IsExpectedIoException(ex))
        {
            // 파일 잠금(아토믹 replace 에디터의 delete→rename 중간 상태) 또는 권한 오류는 흡수.
            LogProvider.Sink?.Debug($"CheckReload mtime probe skipped (file locked or restricted): {ex.Message}");
        }

        return false;
    }

    // 정책 항목 1(타입 좁히기): I/O·권한·JSON·소스젠 비지원 타입만 포획. 훅 로직 버그는 propagate.
    private static bool IsExpectedLoadException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException;

    private static bool IsExpectedSaveException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException;

    private static bool IsExpectedIoException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    // ================================================================
    // Protected virtual hooks — App 레이어 override 지점
    // ================================================================

    /// <summary>
    /// STJ 소스 생성기가 JSON 에 없는 init 속성의 기본값을 보존하지 않을 때,
    /// 역직렬화 직후 모든 참조 타입 하위 객체를 null 체크/보정하는 훅.
    /// 기본 구현은 identity.
    /// </summary>
    protected virtual T ApplyNullSafetyNet(T config) => config;

    /// <summary>
    /// 역직렬화 완료 후, 원본 병합 JSON 문자열을 다시 검사하여 T 에 수동 복구가 필요한
    /// 필드를 재구성하는 훅 (예: 사전형 하위 필드의 수동 재조립).
    /// 기본 구현은 identity.
    /// </summary>
    /// <param name="config">역직렬화 직후의 T.</param>
    /// <param name="mergedJson">MergeWithDefaults 결과 JSON. JsonDocument.Parse 로 재파싱 가능.</param>
    protected virtual T PostDeserializeFixup(T config, string mergedJson) => config;

    /// <summary>
    /// 스키마 버전 기반 마이그레이션 체인. 기본 구현은 identity.
    /// </summary>
    protected virtual T Migrate(T config) => config;

    /// <summary>
    /// 수치 클램핑 등 조용한 범위 보정 훅. 기본 구현은 identity.
    /// </summary>
    protected virtual T Validate(T config) => config;

    /// <summary>
    /// 테마 프리셋 적용 훅 (ThemePresets 등). 기본 구현은 identity.
    /// </summary>
    protected virtual T ApplyTheme(T config) => config;

    /// <summary>
    /// 직렬화 완료 후 JSON 문자열의 포맷을 후처리하는 훅.
    /// 기본 구현은 identity (변환 없음).
    /// </summary>
    protected virtual string FormatJson(string json) => json;

    // ================================================================
    // MergeWithDefaults — T-독립 병합 로직
    // ================================================================

    /// <summary>
    /// 사용자 JSON 파싱 옵션. 소스 생성 컨텍스트의 <c>ReadCommentHandling.Skip</c> +
    /// <c>AllowTrailingCommas</c> 와 일치시킨다. 기본 <see cref="JsonDocumentOptions"/> 는
    /// 둘 다 거부하므로, 주석/트레일링 콤마가 포함된 <b>정상</b> config 가 병합 전처리
    /// 단계에서 <see cref="JsonException"/> 으로 "손상"으로 오판되어 사용자 설정 전체가
    /// 디폴트로 묵음 무시되던 회귀를 차단한다.
    /// </summary>
    private static readonly JsonDocumentOptions UserJsonDocOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 기본 T JSON 과 사용자 JSON 을 재귀적으로 병합한다. 기본값을 기저로 깔고,
    /// 사용자 JSON 의 키가 기본값을 덮어쓴다. 양쪽 모두 객체인 키는 <b>재귀 병합</b>하여,
    /// 사용자가 중첩 객체(예: <c>event_triggers</c>, <c>advanced</c>)를 부분만 지정해도
    /// 누락된 형제 필드가 기본값을 유지하게 한다 — STJ 소스 생성기가 <c>init</c> 기본값을
    /// 보존하지 않아(reflection off) 부분 지정 시 형제 필드가 <c>default(T)</c> 로 리셋되던
    /// 회귀를 차단. (<c>internal</c> 노출은 KoEnVue.Tests 머지 매트릭스 검증용.)
    /// </summary>
    internal static string MergeWithDefaults(string userJson, JsonTypeInfo<T> typeInfo)
    {
        // 기본 T → JSON (모든 init 기본값이 포함됨). Serialize 산물이라 주석/콤마 없음 → 기본 파싱 옵션.
        T defaults = new();
        string defaultJson = JsonSerializer.Serialize(defaults, typeInfo);

        using var defaultDoc = JsonDocument.Parse(defaultJson);
        using var userDoc = JsonDocument.Parse(userJson, UserJsonDocOptions);

        // 설정 파일의 최상위는 반드시 객체다. null / 배열 / 스칼라가 오면 MergeObjects 의
        // TryGetProperty·EnumerateObject 가 JsonElementWrongTypeException(InvalidOperationException)
        // 을 던지는데, 이 타입은 IsExpectedLoadException 필터 **밖**이라 Load 를 뚫고 WndProc
        // 최상위까지 전파된다 — config.json 에 `null` 한 줄만 남겨도 앱이 죽던 경로.
        // 손상으로 분류해 정상 실패 경로(덮어쓰지 않고 이전 설정 유지)로 보낸다.
        if (userDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                $"Config root must be a JSON object, but was {userDoc.RootElement.ValueKind}.");
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            MergeObjects(writer, defaultDoc.RootElement, userDoc.RootElement);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// 두 JSON 객체를 병합해 <paramref name="writer"/> 에 기록한다. 키별 규칙:
    /// <list type="bullet">
    ///   <item>양쪽 모두 객체 → 재귀 병합 (중첩 부분 지정 시 형제 기본값 보존)</item>
    ///   <item>그 외(스칼라·배열·타입 불일치·null) → 사용자 값으로 교체</item>
    ///   <item>기본값에 없는 사용자 전용 키 → 그대로 보존 (동적 dict 키·미래 호환)</item>
    /// </list>
    /// 배열은 의도적으로 통째 교체한다 — 인덱스 단위 병합은 사용자가 리스트 항목을
    /// 줄이려는 의도(예: 필터 리스트 축소)를 막기 때문.
    /// </summary>
    private static void MergeObjects(Utf8JsonWriter writer, JsonElement defaultObj, JsonElement userObj)
    {
        writer.WriteStartObject();
        var writtenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonProperty defProp in defaultObj.EnumerateObject())
        {
            writtenKeys.Add(defProp.Name);

            if (userObj.TryGetProperty(defProp.Name, out JsonElement userVal))
            {
                writer.WritePropertyName(defProp.Name);
                if (defProp.Value.ValueKind == JsonValueKind.Object
                    && userVal.ValueKind == JsonValueKind.Object)
                {
                    MergeObjects(writer, defProp.Value, userVal);
                }
                else
                {
                    userVal.WriteTo(writer);
                }
            }
            else
            {
                defProp.WriteTo(writer);
            }
        }

        // 기본값에 없는 사용자 전용 키(동적 dict 키 등) 보존.
        foreach (JsonProperty userProp in userObj.EnumerateObject())
        {
            if (!writtenKeys.Contains(userProp.Name))
                userProp.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
