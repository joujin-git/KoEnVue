using System.IO;
using System.Text.Json;
using KoEnVue.App.Models;

namespace KoEnVue.App.Config;

/// <summary>
/// 구 <c>cursor_motion_dim_enabled</c> → <c>cursor_display_mode</c> 마이그레이션 (PR-31).
/// 원본 user JSON 기준으로 새 키 유무를 판별한다 (mergedJson 은 디폴트가 섞여 부정확).
/// </summary>
internal static class CursorDisplayModeMigration
{
    public const string NewKey = "cursor_display_mode";
    public const string LegacyKey = "cursor_motion_dim_enabled";

    /// <summary>
    /// user 루트에서 마이그 필요 시 <paramref name="mode"/> 를 채우고 true.
    /// 새 키가 이미 있으면 false (역직렬화 값 유지).
    /// </summary>
    public static bool TryResolveFromUserRoot(JsonElement root, out CursorDisplayMode mode)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(NewKey, out _))
        {
            mode = default;
            return false;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(LegacyKey, out JsonElement legacy)
            && (legacy.ValueKind == JsonValueKind.True || legacy.ValueKind == JsonValueKind.False))
        {
            // true = 기존 "이동 중 옅게 ON" → Motion. false = 끔 → Sharp.
            mode = legacy.GetBoolean() ? CursorDisplayMode.Motion : CursorDisplayMode.Sharp;
            return true;
        }

        // 둘 다 없음 → Soft (신규 디폴트). 역직렬화가 이미 Soft여도 명시 적용.
        mode = CursorDisplayMode.Soft;
        return true;
    }

    /// <summary>user 파일 경로에서 마이그. 파일 없음/파싱 실패 시 Soft 적용(true).</summary>
    public static bool TryResolveFromUserFile(string filePath, out CursorDisplayMode mode)
    {
        mode = CursorDisplayMode.Soft;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return true;

        try
        {
            string text = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(text);
            return TryResolveFromUserRoot(doc.RootElement, out mode);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            mode = CursorDisplayMode.Soft;
            return true;
        }
    }
}
