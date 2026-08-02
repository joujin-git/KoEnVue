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
    /// **정규 로드 경로와 같은 관용도**로 읽어야 한다 (bug-hunt 3차 M). 이 프로젝트는 config.json 의
    /// 주석과 트레일링 콤마를 정상으로 취급하는데(<c>AppConfigJsonContext</c> · <c>Core</c> 양쪽 모두),
    /// 여기만 기본 <see cref="JsonDocumentOptions"/> 를 써서 그런 파일을 파싱 실패로 처리했다.
    /// </summary>
    private static readonly JsonDocumentOptions UserFileOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

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

    /// <summary>
    /// user 파일 경로에서 마이그. 파일이 없으면 신규 설치이므로 Soft 적용(true).
    ///
    /// <para>
    /// <b>읽지 못하면 false 다</b> (bug-hunt 3차 M). 종전에는 파싱 실패에도 <c>Soft</c> + true 를
    /// 돌려줬는데, 이 함수는 <c>PostDeserializeFixup</c> 에서 **매 로드마다** 불리므로 그 결과가
    /// 곧바로 사용자의 <c>cursor_display_mode</c> 를 덮어썼다. 게다가 파싱은 기본 옵션이라
    /// **주석 한 줄만 있어도** 실패했다 — 이 프로젝트가 정상으로 취급하는 파일이다. 결과적으로
    /// 주석을 쓴 사용자가 <c>sharp</c>/<c>motion</c> 을 지정하면 매 로드마다 <c>soft</c> 로
    /// 되돌아가고, 다음 저장이 그것을 디스크에 확정했다. 읽을 수 없는 파일은 판정의 근거가 될 수
    /// 없으므로 역직렬화된 값을 그대로 둔다.
    /// </para>
    /// </summary>
    public static bool TryResolveFromUserFile(string filePath, out CursorDisplayMode mode)
    {
        mode = CursorDisplayMode.Soft;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return true;

        try
        {
            string text = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(text, UserFileOptions);
            return TryResolveFromUserRoot(doc.RootElement, out mode);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
