using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoEnVue.Detector;
using KoEnVue.Models;
using KoEnVue.Native;
using KoEnVue.Utils;

namespace KoEnVue.Config;

/// <summary>
/// config.json 로드/저장/핫 리로드/마이그레이션/검증 + 앱별 프로필.
/// volatile AppConfig 참조 교체 패턴으로 스레드 안전.
/// </summary>
internal static class Settings
{
    // ================================================================
    // 상수
    // ================================================================

    /// <summary>현재 config 스키마 버전.</summary>
    public const int CurrentVersion = 1;

    // ================================================================
    // 상태
    // ================================================================

    private static string? _configFilePath;
    private static DateTime _lastConfigMtime = DateTime.MinValue;

    // 앱 프로필 LRU 캐시 (감지 스레드 읽기 + 메인 스레드 클리어)
    private static readonly Dictionary<string, AppConfig?> _profileCache = new();
    private static readonly LinkedList<string> _profileLruOrder = new();
    private static readonly object _profileCacheLock = new();

    // ================================================================
    // Public 속성
    // ================================================================

    /// <summary>현재 활성 config 파일 경로. null이면 기본값 사용 중.</summary>
    public static string? ConfigFilePath => _configFilePath;

    /// <summary>포터블 모드 여부. exe 디렉토리에 config.json이 있으면 true.</summary>
    public static bool IsPortableMode =>
        _configFilePath is not null &&
        _configFilePath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);

    // ================================================================
    // Load — 3-tier 탐색
    // ================================================================

    /// <summary>
    /// 설정 파일 탐색: %APPDATA% → exe dir → 기본값.
    /// BOM 제거 + Migrate + Validate 파이프라인 적용.
    /// </summary>
    public static AppConfig Load()
    {
        string[] candidates =
        [
            DefaultConfig.GetDefaultConfigPath(),
            Path.Combine(AppContext.BaseDirectory, DefaultConfig.ConfigFileName),
        ];

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;

                AppConfig config = LoadFromFile(path);
                _configFilePath = path;
                _lastConfigMtime = File.GetLastWriteTimeUtc(path);
                Logger.Info($"Config loaded from {path}");
                return config;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to load config from {path}: {ex.Message}");
            }
        }

        _configFilePath = null;
        Logger.Info("Using default config");
        return new AppConfig();
    }

    /// <summary>
    /// 파일에서 설정 로드. BOM 제거 → 기본값 병합 → 역직렬화 → Migrate → Validate.
    /// </summary>
    private static AppConfig LoadFromFile(string path)
    {
        string json = File.ReadAllText(path, Encoding.UTF8);
        // UTF-8 BOM 감지 → 제거
        if (json.Length > 0 && json[0] == '\uFEFF')
            json = json[1..];

        // .NET 10 STJ source gen workaround:
        // record init 기본값이 역직렬화 시 보존되지 않음 (소스 생성기 제한).
        // 해결: 기본 config를 JSON으로 직렬화 → 사용자 JSON 병합 → 역직렬화.
        string mergedJson = MergeWithDefaults(json);

        AppConfig? config = JsonSerializer.Deserialize(mergedJson, AppConfigJsonContext.Default.AppConfig);
        if (config is null)
            return new AppConfig();

        config = EnsureSubObjects(config);
        config = Migrate(config);
        config = Validate(config);
        config = ThemePresets.Apply(config);
        return config;
    }

    /// <summary>
    /// 기본 AppConfig JSON과 사용자 JSON을 병합한다.
    /// 기본값을 기저로 깔고, 사용자 JSON의 키가 기본값을 덮어쓴다.
    /// .NET 10 STJ 소스 생성기가 record init 기본값을 보존하지 않는 문제의 우회책.
    /// </summary>
    private static string MergeWithDefaults(string userJson)
    {
        // 기본 config → JSON (모든 init 기본값이 포함됨)
        string defaultJson = JsonSerializer.Serialize(new AppConfig(), AppConfigJsonContext.Default.AppConfig);

        using var defaultDoc = JsonDocument.Parse(defaultJson);
        using var userDoc = JsonDocument.Parse(userJson);

        // 사용자 JSON 키 수집
        var userKeys = new HashSet<string>();
        foreach (var prop in userDoc.RootElement.EnumerateObject())
            userKeys.Add(prop.Name);

        // 병합: 기본값(사용자 JSON에 없는 키만) + 사용자 JSON(전체)
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in defaultDoc.RootElement.EnumerateObject())
            {
                if (!userKeys.Contains(prop.Name))
                    prop.WriteTo(writer);
            }
            foreach (var prop in userDoc.RootElement.EnumerateObject())
                prop.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ================================================================
    // Save — NF-25: 저장 실패 시 인메모리 유지
    // ================================================================

    /// <summary>
    /// config.json 저장. path 미지정 시 현재 활성 경로 또는 기본 경로 사용.
    /// </summary>
    public static void Save(AppConfig config, string? path = null)
    {
        path ??= _configFilePath ?? DefaultConfig.GetDefaultConfigPath();

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            File.WriteAllText(path, json, Encoding.UTF8);

            // 저장 후 mtime 갱신 (핫 리로드 중복 감지 방지)
            _lastConfigMtime = File.GetLastWriteTimeUtc(path);
            _configFilePath ??= path;

            Logger.Debug($"Config saved to {path}");
        }
        catch (Exception ex)
        {
            // NF-25: 저장 실패 시 로그 경고 + 인메모리 유지
            Logger.Warning($"Failed to save config: {ex.Message}");
        }
    }

    // ================================================================
    // Validate — 범위 클램핑
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
            CaretPollIntervalMs = Math.Clamp(config.CaretPollIntervalMs, 30, 500),

            // 표시
            EventDisplayDurationMs = Math.Clamp(config.EventDisplayDurationMs, 500, 10000),
            AlwaysIdleTimeoutMs = Math.Clamp(config.AlwaysIdleTimeoutMs, 1000, 30000),

            // 투명도
            Opacity = Math.Clamp(config.Opacity, 0.1, 1.0),
            IdleOpacity = Math.Clamp(config.IdleOpacity, 0.1, 1.0),
            ActiveOpacity = Math.Clamp(config.ActiveOpacity, 0.1, 1.0),
            CaretBoxOpacity = Math.Clamp(config.CaretBoxOpacity, 0.1, 1.0),
            CaretBoxIdleOpacity = Math.Clamp(config.CaretBoxIdleOpacity, 0.1, 1.0),
            CaretBoxActiveOpacity = Math.Clamp(config.CaretBoxActiveOpacity, 0.1, 1.0),
            CaretBoxMinOpacity = Math.Clamp(config.CaretBoxMinOpacity, 0.1, 1.0),

            // 애니메이션
            HighlightScale = Math.Clamp(config.HighlightScale, 1.0, 2.0),
            FadeInMs = Math.Clamp(config.FadeInMs, 0, 2000),
            FadeOutMs = Math.Clamp(config.FadeOutMs, 0, 2000),
            HighlightDurationMs = Math.Clamp(config.HighlightDurationMs, 0, 2000),
            SlideSpeedMs = Math.Clamp(config.SlideSpeedMs, 0, 2000),

            // 크기
            FontSize = Math.Clamp(config.FontSize, 8, 36),
            CaretDotSize = Math.Clamp(config.CaretDotSize, 4, 32),
            CaretSquareSize = Math.Clamp(config.CaretSquareSize, 4, 32),
            CaretUnderlineWidth = Math.Clamp(config.CaretUnderlineWidth, 8, 64),
            CaretUnderlineHeight = Math.Clamp(config.CaretUnderlineHeight, 1, 16),
            CaretVbarWidth = Math.Clamp(config.CaretVbarWidth, 1, 16),
            CaretVbarHeight = Math.Clamp(config.CaretVbarHeight, 4, 64),
            LabelWidth = Math.Clamp(config.LabelWidth, 16, 128),
            LabelHeight = Math.Clamp(config.LabelHeight, 12, 96),
            LabelBorderRadius = Math.Clamp(config.LabelBorderRadius, 0, 48),
            BorderWidth = Math.Clamp(config.BorderWidth, 0, 8),
            ScreenEdgeMargin = Math.Clamp(config.ScreenEdgeMargin, 0, 50),

            // 시스템
            LogMaxSizeMb = Math.Clamp(config.LogMaxSizeMb, 1, 100),
        };
    }

    // ================================================================
    // EnsureSubObjects — STJ 소스 생성기 null 보정
    // ================================================================

    /// <summary>
    /// System.Text.Json 소스 생성기는 JSON에 없는 init 속성의 기본값을 보존하지 않을 수 있다.
    /// 역직렬화 직후 모든 참조 타입 하위 객체를 null 체크하여 기본값으로 보정.
    /// </summary>
    private static AppConfig EnsureSubObjects(AppConfig config)
    {
        return config with
        {
            EventTriggers = config.EventTriggers ?? new(),
            FixedPosition = config.FixedPosition ?? new(),
            CaretOffset = config.CaretOffset ?? new() { X = -2, Y = 0 },
            MouseOffset = config.MouseOffset ?? new() { X = 20, Y = 25 },
            Advanced = config.Advanced ?? new(),
            SystemHideClasses = config.SystemHideClasses ?? ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"],
            SystemHideClassesUser = config.SystemHideClassesUser ?? [],
            AppProfiles = config.AppProfiles ?? new(),
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
            HotkeyCycleStyle = config.HotkeyCycleStyle ?? "Ctrl+Alt+I",
            HotkeyCyclePosition = config.HotkeyCyclePosition ?? "Ctrl+Alt+P",
            HotkeyCycleDisplay = config.HotkeyCycleDisplay ?? "Ctrl+Alt+D",
            HotkeyOpenSettings = config.HotkeyOpenSettings ?? "Ctrl+Alt+S",
            Language = config.Language ?? "ko",
            LogFilePath = config.LogFilePath ?? "",
        };
    }

    // ================================================================
    // Migrate — config_version 체인
    // ================================================================

    /// <summary>
    /// config_version 기반 마이그레이션 체인. 어떤 버전에서든 최신으로 순차 적용.
    /// </summary>
    public static AppConfig Migrate(AppConfig config)
    {
        int version = config.ConfigVersion;

        // 향후 확장:
        // if (version < 2) config = MigrateV1ToV2(config);
        // if (version < 3) config = MigrateV2ToV3(config);

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
        if (_configFilePath is null) return;

        try
        {
            DateTime mtime = File.GetLastWriteTimeUtc(_configFilePath);
            if (mtime != _lastConfigMtime)
            {
                _lastConfigMtime = mtime;
                User32.PostMessageW(hwndMain, AppMessages.WM_CONFIG_CHANGED,
                    IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 파일 접근 실패 시 무시
        }
    }

    // ================================================================
    // OpenSettingsFile — 에디터에서 열기
    // ================================================================

    /// <summary>
    /// config.json을 시스템 기본 편집기로 연다. 파일 미존재 시 기본 설정으로 생성.
    /// </summary>
    public static void OpenSettingsFile(string? overridePath = null)
    {
        string path = overridePath ?? _configFilePath ?? DefaultConfig.GetDefaultConfigPath();

        try
        {
            if (!File.Exists(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null)
                    Directory.CreateDirectory(dir);

                var defaultConfig = new AppConfig();
                string json = JsonSerializer.Serialize(defaultConfig, AppConfigJsonContext.Default.AppConfig);
                File.WriteAllText(path, json, Encoding.UTF8);
                _configFilePath ??= path;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to open settings file: {ex.Message}");
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
            if (_profileCache.Count >= DefaultConfig.AppMethodCacheMaxSize)
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
            AppProfileMatch.Process => SystemFilter.GetProcessName(hwnd).ToLowerInvariant(),
            AppProfileMatch.Class => SystemFilter.GetClassName(hwnd),
            AppProfileMatch.Title => GetWindowTitle(hwnd),
            _ => SystemFilter.GetProcessName(hwnd).ToLowerInvariant(),
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
            return merged is not null ? EnsureSubObjects(merged) : global;
        }
        catch
        {
            Logger.Warning("Failed to merge app profile, using global config");
            return global;
        }
    }
}
