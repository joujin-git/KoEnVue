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
    private DateTime _lastMtime = DateTime.MinValue;

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
        if (File.Exists(_filePath))
        {
            try
            {
                string userJson = JsonSettingsFile.ReadAllTextStripBom(_filePath);
                string mergedJson = MergeWithDefaults(userJson, _typeInfo);

                T? config = JsonSerializer.Deserialize(mergedJson, _typeInfo);
                if (config is null)
                    return new T();

                config = ApplyNullSafetyNet(config);
                config = PostDeserializeFixup(config, mergedJson);
                config = Migrate(config);
                config = Validate(config);
                config = ApplyTheme(config);

                _lastMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
                LogProvider.Sink?.Info($"Config loaded from {_filePath}");
                return config;
            }
            catch (Exception ex) when (IsExpectedLoadException(ex))
            {
                // 파일은 존재하지만 파싱 실패 — 사용자 복구 가능성을 위해 덮어쓰지 않음.
                // mtime은 갱신해서 5초 폴링이 WM_CONFIG_CHANGED를 무한 재발송하는 스팸을 차단.
                // 정책 항목 1(타입 좁히기): I/O + JSON + 소스젠 비지원 타입만 잡고, 훅(Validate/Migrate/...)
                // 의 로직 버그(NullRef 등)는 propagate 시켜 표면화하도록 한다.
                LogProvider.Sink?.Warning($"Failed to load config from {_filePath}: {ex.Message}. Using defaults without overwriting.");
                try { _lastMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath); }
                catch (Exception innerEx) when (IsExpectedIoException(innerEx)) { }
                return new T();
            }
        }

        LogProvider.Sink?.Info($"Config not found, creating defaults at {_filePath}");
        T defaults = new();
        Save(defaults);
        return defaults;
    }

    // ================================================================
    // Save — NF-25: 저장 실패 시 인메모리 유지
    // ================================================================

    /// <summary>
    /// 현재 경로로 저장. 실패 시 경고 로그만 남기고 예외를 삼킨다(인메모리 유지).
    /// 성공 시 mtime 을 갱신해 자체 호출에 의한 핫 리로드 루프를 차단.
    /// </summary>
    public void Save(T value)
    {
        try
        {
            string json = JsonSerializer.Serialize(value, _typeInfo);
            json = FormatJson(json);
            JsonSettingsFile.WriteAllText(_filePath, json);

            _lastMtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
            LogProvider.Sink?.Debug($"Config saved to {_filePath}");
        }
        catch (Exception ex) when (IsExpectedSaveException(ex))
        {
            // NF-25: 저장 실패 시 로그 경고 + 인메모리 유지.
            // 정책 항목 1(타입 좁히기): 로직 버그(NullRef 등)는 전파되어 표면화되도록 한다.
            LogProvider.Sink?.Warning($"Failed to save config: {ex.Message}");
        }
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
            DateTime mtime = JsonSettingsFile.GetLastWriteTimeUtc(_filePath);
            if (mtime != _lastMtime)
            {
                _lastMtime = mtime;
                return true;
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
