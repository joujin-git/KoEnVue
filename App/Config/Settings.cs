using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using KoEnVue.App.Models;
using KoEnVue.App.UI;
using KoEnVue.Core.Config;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;
using KoEnVue.Core.Windowing;

namespace KoEnVue.App.Config;

/// <summary>
/// config.json 로드/저장/핫 리로드/마이그레이션/검증 + 앱별 프로필.
/// 파이프라인 본체(MergeWithDefaults → Deserialize → EnsureSubObjects →
/// EnsureIndicatorPositions → Migrate → Validate → ApplyTheme)는
/// <see cref="JsonSettingsManager{T}"/> 가 Core 레벨에서 수행하고,
/// 본 정적 파사드는 AppConfig 특화 훅 바인딩과 앱 프로필 LRU 캐시만 담당한다.
/// </summary>
internal static class Settings
{
    // ================================================================
    // 상수
    // ================================================================

    /// <summary>현재 config 스키마 버전.</summary>
    public const int CurrentVersion = 4;

    /// <summary>앱 프로필 LRU 캐시 최대 크기.</summary>
    private const int ProfileCacheMaxSize = 50;

    // ================================================================
    // 상태
    // ================================================================

    private static AppSettingsManager? _manager;

    // 앱 프로필 LRU 캐시 (감지 스레드 읽기 + 메인 스레드 클리어)
    private static readonly Dictionary<string, AppConfig?> _profileCache = new();
    private static readonly LinkedList<string> _profileLruOrder = new();
    private static readonly object _profileCacheLock = new();

    // ================================================================
    // Public 속성
    // ================================================================

    /// <summary>현재 활성 config 파일 경로. Load 이후에는 항상 exe 디렉토리의 config.json 경로.</summary>
    public static string? ConfigFilePath => _manager?.FilePath;

    // ================================================================
    // Load — exe 디렉토리 단일 경로
    // ================================================================

    /// <summary>
    /// exe 디렉토리의 config.json을 로드.
    /// - 파일 존재 + 정상: 로드
    /// - 파일 존재 + 파싱 실패: 덮어쓰지 않고 기본값 반환 (복구 가능성 보존). mtime 갱신으로 폴링 스팸 차단.
    /// - 파일 없음: 기본값을 즉시 디스크에 생성 (포터블 UX: exe만 있어도 config.json이 바로 나타남).
    /// </summary>
    public static AppConfig Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, DefaultConfig.ConfigFileName);
        _manager ??= new AppSettingsManager(path, AppConfigJsonContext.Default.AppConfig);
        return _manager.Load();
    }

    // ================================================================
    // Save — NF-25: 저장 실패 시 인메모리 유지
    // ================================================================

    /// <summary>
    /// config.json 저장. path 미지정 시 현재 활성 경로 또는 기본 경로 사용.
    /// </summary>
    public static void Save(AppConfig config, string? path = null)
    {
        if (_manager is null || (path is not null && path != _manager.FilePath))
        {
            string targetPath = path ?? Path.Combine(AppContext.BaseDirectory, DefaultConfig.ConfigFileName);
            _manager = new AppSettingsManager(targetPath, AppConfigJsonContext.Default.AppConfig);
        }
        _manager.Save(config);
    }

    // ================================================================
    // Validate — 범위 클램핑 (앱 프로필 머지 등에서 직접 호출됨)
    // ================================================================

    /// <summary>
    /// 모든 수치 설정값을 유효 범위로 클램핑. 에러 아닌 조용한 보정.
    /// </summary>
    public static AppConfig Validate(AppConfig config)
    {
        return config with
        {
            // 감지
            PollIntervalMs = Math.Clamp(config.PollIntervalMs, 50, 500),

            // 표시
            EventDisplayDurationMs = Math.Clamp(config.EventDisplayDurationMs, 500, 10000),
            AlwaysIdleTimeoutMs = Math.Clamp(config.AlwaysIdleTimeoutMs, 1000, 30000),

            // 투명도
            Opacity = Math.Clamp(config.Opacity, 0.1, 1.0),
            IdleOpacity = Math.Clamp(config.IdleOpacity, 0.1, 1.0),
            ActiveOpacity = Math.Clamp(config.ActiveOpacity, 0.1, 1.0),

            // 애니메이션
            HighlightScale = Math.Clamp(config.HighlightScale, 1.0, 2.0),
            FadeInMs = Math.Clamp(config.FadeInMs, 0, 2000),
            FadeOutMs = Math.Clamp(config.FadeOutMs, 0, 2000),
            HighlightDurationMs = Math.Clamp(config.HighlightDurationMs, 0, 2000),
            SlideSpeedMs = Math.Clamp(config.SlideSpeedMs, 0, 2000),

            // 크기
            FontSize = Math.Clamp(config.FontSize, 8, 36),
            LabelWidth = Math.Clamp(config.LabelWidth, 16, 128),
            LabelHeight = Math.Clamp(config.LabelHeight, 12, 96),
            LabelBorderRadius = Math.Clamp(config.LabelBorderRadius, 0, 48),
            BorderWidth = Math.Clamp(config.BorderWidth, 0, 8),
            IndicatorScale = Math.Round(Math.Clamp(config.IndicatorScale, 1.0, 5.0), 1),

            // 시스템
            LogMaxSizeMb = Math.Clamp(config.LogMaxSizeMb, 1, 100),
        };
    }

    // ================================================================
    // Migrate — config_version 체인 (앱 프로필 머지 등에서 직접 호출됨)
    // ================================================================

    /// <summary>
    /// config_version 기반 마이그레이션 체인. 어떤 버전에서든 최신으로 순차 적용.
    /// </summary>
    public static AppConfig Migrate(AppConfig config)
    {
        int version = config.ConfigVersion;

        // v2 → v3: system_edit_focus_classes 필드 제거 (바탕화면 리네임 escape hatch 폐기).
        // 필드 자체가 AppConfig에서 삭제됐으므로 STJ가 JSON 키를 무시하고,
        // 다음 Save에서 자동으로 파일에서 사라진다. 여기선 버전만 갱신.

        // v3 → v4: 바탕화면 우클릭 컨텍스트 메뉴(Win11) 숨김 클래스 추가.
        // 기존 사용자의 system_hide_classes에 XamlExplorerHostIslandWindow_WASDK가 없으면 추가.
        if (version < 4)
        {
            const string desktopMenuClass = "XamlExplorerHostIslandWindow_WASDK";
            string[] classes = config.SystemHideClasses;
            bool found = false;
            foreach (string c in classes)
            {
                if (c.Equals(desktopMenuClass, StringComparison.OrdinalIgnoreCase))
                { found = true; break; }
            }
            if (!found)
            {
                string[] updated = new string[classes.Length + 1];
                classes.CopyTo(updated, 0);
                updated[classes.Length] = desktopMenuClass;
                config = config with { SystemHideClasses = updated };
            }
        }

        return config with { ConfigVersion = CurrentVersion };
    }

    // ================================================================
    // CheckConfigFileChange — 5초 mtime 체크
    // ================================================================

    /// <summary>
    /// config.json mtime 변경 감지. 변경 시 WM_CONFIG_CHANGED PostMessage.
    /// 감지 스레드에서 ~5초마다 호출.
    /// </summary>
    public static void CheckConfigFileChange(IntPtr hwndMain)
    {
        if (_manager is null) return;
        if (_manager.CheckReload())
        {
            User32.PostMessageW(hwndMain, AppMessages.WM_CONFIG_CHANGED,
                IntPtr.Zero, IntPtr.Zero);
        }
    }

    // ================================================================
    // ResolveForApp — 앱별 프로필 매칭 + LRU 캐시
    // ================================================================

    /// <summary>
    /// 앱별 프로필 적용. 매칭된 프로필의 키만 오버라이드, 나머지 상속.
    /// null 반환 = enabled: false (해당 앱에서 인디케이터 비활성화).
    /// </summary>
    public static AppConfig? ResolveForApp(AppConfig global, IntPtr hwnd)
    {
        if (global.AppProfiles.Count == 0) return global;

        string key = ResolveMatchKey(global, hwnd);
        if (string.IsNullOrEmpty(key)) return global;

        // LRU 캐시 조회
        lock (_profileCacheLock)
        {
            if (_profileCache.TryGetValue(key, out AppConfig? cached))
            {
                // LRU 순서 갱신
                _profileLruOrder.Remove(key);
                _profileLruOrder.AddFirst(key);
                return cached;
            }
        }

        // 프로필 매칭
        AppConfig? resolved = MatchProfile(global, key);

        // 캐시 저장
        lock (_profileCacheLock)
        {
            if (_profileCache.Count >= ProfileCacheMaxSize)
            {
                string oldest = _profileLruOrder.Last!.Value;
                _profileLruOrder.RemoveLast();
                _profileCache.Remove(oldest);
            }
            _profileCache[key] = resolved;
            _profileLruOrder.AddFirst(key);
        }

        return resolved;
    }

    /// <summary>
    /// LRU 캐시 전체 초기화. config 리로드 시 호출.
    /// </summary>
    public static void ClearProfileCache()
    {
        lock (_profileCacheLock)
        {
            _profileCache.Clear();
            _profileLruOrder.Clear();
        }
    }

    // ================================================================
    // Private — 프로필 매칭 헬퍼
    // ================================================================

    /// <summary>
    /// app_profile_match 설정에 따라 매칭 키를 추출.
    /// </summary>
    private static string ResolveMatchKey(AppConfig config, IntPtr hwnd)
    {
        return config.AppProfileMatch switch
        {
            AppProfileMatch.Process => WindowProcessInfo.GetProcessName(hwnd).ToLowerInvariant(),
            AppProfileMatch.Class => WindowProcessInfo.GetClassName(hwnd),
            AppProfileMatch.Title => GetWindowTitle(hwnd),
            _ => WindowProcessInfo.GetProcessName(hwnd).ToLowerInvariant(),
        };
    }

    /// <summary>
    /// 윈도우 타이틀 조회.
    /// </summary>
    private static string GetWindowTitle(IntPtr hwnd)
    {
        char[] buffer = new char[Win32Constants.MAX_WINDOW_TEXT];
        int len = User32.GetWindowTextW(hwnd, buffer, Win32Constants.MAX_WINDOW_TEXT);
        return len > 0 ? new string(buffer, 0, len) : string.Empty;
    }

    /// <summary>
    /// 프로필 딕셔너리에서 키 매칭. process/class: 직접 조회, title: Regex 패턴.
    /// enabled: false → null 반환.
    /// </summary>
    private static AppConfig? MatchProfile(AppConfig global, string key)
    {
        if (global.AppProfileMatch == AppProfileMatch.Title)
        {
            // title 모드: 각 프로필 키를 Regex 패턴으로 매칭
            // NativeAOT: RegexOptions.Compiled 금지 (Reflection.Emit 불가)
            foreach (var (pattern, profile) in global.AppProfiles)
            {
                try
                {
                    if (Regex.IsMatch(key, pattern, RegexOptions.IgnoreCase))
                    {
                        if (IsDisabledProfile(profile)) return null;
                        return MergeProfile(global, profile);
                    }
                }
                catch
                {
                    // 잘못된 정규식 패턴 무시
                }
            }
            return global;
        }

        // process/class 모드: 직접 Dictionary 조회
        if (!global.AppProfiles.TryGetValue(key, out JsonElement profile2))
            return global;

        if (IsDisabledProfile(profile2)) return null;
        return MergeProfile(global, profile2);
    }

    /// <summary>
    /// 프로필 JsonElement에 "enabled": false가 있는지 확인.
    /// </summary>
    private static bool IsDisabledProfile(JsonElement profile)
    {
        return profile.ValueKind == JsonValueKind.Object &&
               profile.TryGetProperty("enabled", out JsonElement enabled) &&
               enabled.ValueKind == JsonValueKind.False;
    }

    /// <summary>
    /// global config에 프로필 오버라이드를 JSON 레벨에서 머지.
    /// 프로필에 명시된 키만 오버라이드, 나머지는 global 상속.
    /// </summary>
    private static AppConfig MergeProfile(AppConfig global, JsonElement profile)
    {
        try
        {
            // 1. global → JSON
            string globalJson = JsonSerializer.Serialize(global, AppConfigJsonContext.Default.AppConfig);
            using var globalDoc = JsonDocument.Parse(globalJson);

            // 2. Utf8JsonWriter로 머지
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in globalDoc.RootElement.EnumerateObject())
                {
                    if (profile.TryGetProperty(prop.Name, out JsonElement overrideValue))
                    {
                        writer.WritePropertyName(prop.Name);
                        overrideValue.WriteTo(writer);
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }

            // 3. 머지된 JSON → AppConfig
            string mergedJson = Encoding.UTF8.GetString(stream.ToArray());
            AppConfig? merged = JsonSerializer.Deserialize(mergedJson, AppConfigJsonContext.Default.AppConfig);
            return merged is not null ? AppSettingsManager.EnsureSubObjectsPublic(merged) : global;
        }
        catch
        {
            Logger.Warning("Failed to merge app profile, using global config");
            return global;
        }
    }
}

// ================================================================
// AppSettingsManager — JsonSettingsManager<AppConfig> 하위 클래스
// ================================================================

/// <summary>
/// AppConfig 전용 <see cref="JsonSettingsManager{T}"/> 서브클래스.
/// 5 개의 파이프라인 훅을 AppConfig-specific 로 구현한다.
/// Core 레이어는 AppConfig 스키마를 몰라야 하므로 이 클래스는 App/Config/ 에 위치.
/// </summary>
internal sealed class AppSettingsManager : JsonSettingsManager<AppConfig>
{
    public AppSettingsManager(string filePath, JsonTypeInfo<AppConfig> typeInfo)
        : base(filePath, typeInfo)
    {
    }

    /// <summary>
    /// STJ 소스 생성기는 JSON에 없는 init 속성의 기본값을 보존하지 않을 수 있다.
    /// 역직렬화 직후 모든 참조 타입 하위 객체를 null 체크하여 기본값으로 보정.
    /// </summary>
    protected override AppConfig ApplyNullSafetyNet(AppConfig config) => EnsureSubObjects(config);

    /// <summary>
    /// NativeAOT STJ source gen이 Dictionary&lt;string, int[]&gt; 역직렬화에 실패할 수 있음.
    /// JSON에서 indicator_positions를 수동 파싱하여 보정.
    /// </summary>
    protected override AppConfig PostDeserializeFixup(AppConfig config, string mergedJson)
    {
        // STJ가 정상 파싱했으면 건너뛰기
        if (config.IndicatorPositions.Count > 0)
            return config;

        try
        {
            using var doc = JsonDocument.Parse(mergedJson);
            if (!doc.RootElement.TryGetProperty("indicator_positions", out JsonElement posElement))
                return config;
            if (posElement.ValueKind != JsonValueKind.Object)
                return config;

            var positions = new Dictionary<string, int[]>();
            foreach (var prop in posElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() >= 2)
                {
                    int x = prop.Value[0].GetInt32();
                    int y = prop.Value[1].GetInt32();
                    positions[prop.Name] = [x, y];
                }
            }

            if (positions.Count > 0)
            {
                Logger.Info($"Manual parse recovered {positions.Count} indicator position(s)");
                return config with { IndicatorPositions = positions };
            }
        }
        catch
        {
            // 수동 파싱 실패 시 무시
        }

        return config;
    }

    protected override AppConfig Migrate(AppConfig config) => Settings.Migrate(config);

    protected override AppConfig Validate(AppConfig config) => Settings.Validate(config);

    protected override AppConfig ApplyTheme(AppConfig config) => ThemePresets.Apply(config);

    // ================================================================
    // EnsureSubObjects — AppConfig null 보정 로직 (MergeProfile 경로에서도 재사용)
    // ================================================================

    /// <summary>
    /// <c>Settings.MergeProfile</c> 경로에서도 동일한 null 보정이 필요해서
    /// 정적 경유 접근점을 제공한다.
    /// </summary>
    public static AppConfig EnsureSubObjectsPublic(AppConfig config) => EnsureSubObjects(config);

    /// <summary>
    /// System.Text.Json 소스 생성기는 JSON에 없는 init 속성의 기본값을 보존하지 않을 수 있다.
    /// 역직렬화 직후 모든 참조 타입 하위 객체를 null 체크하여 기본값으로 보정.
    /// </summary>
    private static AppConfig EnsureSubObjects(AppConfig config)
    {
        return config with
        {
            EventTriggers = config.EventTriggers ?? new(),
            Advanced = config.Advanced ?? new(),
            SystemHideClasses = config.SystemHideClasses ?? ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "XamlExplorerHostIslandWindow_WASDK"],
            SystemHideClassesUser = config.SystemHideClassesUser ?? [],
            SystemHideProcesses = config.SystemHideProcesses ?? ["ShellExperienceHost"],
            SystemHideProcessesUser = config.SystemHideProcessesUser ?? [],
            AppProfiles = config.AppProfiles ?? new(),
            IndicatorPositions = config.IndicatorPositions ?? new(),
            AppFilterList = config.AppFilterList ?? [],
            TrayQuickOpacityPresets = config.TrayQuickOpacityPresets ?? [0.95, 0.85, 0.6],
            BorderColor = config.BorderColor ?? "#000000",
            HangulBg = config.HangulBg ?? "#16A34A",
            HangulFg = config.HangulFg ?? "#FFFFFF",
            EnglishBg = config.EnglishBg ?? "#D97706",
            EnglishFg = config.EnglishFg ?? "#FFFFFF",
            NonKoreanBg = config.NonKoreanBg ?? "#6B7280",
            NonKoreanFg = config.NonKoreanFg ?? "#FFFFFF",
            FontFamily = config.FontFamily ?? "맑은 고딕",
            HangulLabel = config.HangulLabel ?? "한",
            EnglishLabel = config.EnglishLabel ?? "En",
            NonKoreanLabel = config.NonKoreanLabel ?? "EN",
            HotkeyToggleVisibility = config.HotkeyToggleVisibility ?? "Ctrl+Alt+H",
            Language = config.Language ?? "ko",
            LogFilePath = config.LogFilePath ?? "",
        };
    }
}
