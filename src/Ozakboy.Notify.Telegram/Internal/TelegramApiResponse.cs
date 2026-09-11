using System.Text.Json;

namespace Ozakboy.Notify.Telegram.Internal;

/// <summary>
/// Telegram Bot API 回應中本套件在意的欄位。
/// The fields of a Telegram Bot API response this package cares about.
/// </summary>
/// <remarks>
/// 只挑需要的欄位讀,不做完整的物件對映:回應本體裡還有訊息內容的回聲,那些不該被留下來,
/// 也就不該有型別去承載它們。
/// Only the needed fields are read, with no full object mapping: the body also echoes the message content back,
/// which should not be kept around, so there should be no type to carry it either.
/// </remarks>
internal sealed record TelegramApiResponse(
    bool Ok,
    int? ErrorCode,
    string? Description,
    TimeSpan? RetryAfter,
    long? MigrateToChatId)
{
    /// <summary>
    /// 採信的 <c>retry_after</c> 上限:一天。只是為了防止換算溢位,實際等待上限由設定決定。
    /// The ceiling on an honoured <c>retry_after</c>: one day. It only guards the conversion against overflow;
    /// the real wait ceiling comes from the options.
    /// </summary>
    private const long RetryAfterCeilingSeconds = 86_400;

    /// <summary>
    /// 說明文字保留的最大長度。Telegram 的說明通常很短,超過多半是中間代理伺服器塞進來的東西。
    /// The longest description kept. Telegram's are short; anything longer is usually an intermediate proxy's
    /// output.
    /// </summary>
    private const int MaxDescriptionLength = 512;

    /// <summary>
    /// 解析回應本體。不是 Telegram 格式的 JSON 物件時回傳 <see langword="null"/>,而不是擲出例外 ——
    /// 錯誤處理路徑上再拋一個例外,會把真正的失敗原因蓋掉。
    /// Parses a response body, returning <see langword="null"/> rather than throwing when it is not a Telegram
    /// JSON object: a second exception on the error path would bury the real cause.
    /// </summary>
    public static TelegramApiResponse? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;

            int? errorCode = root.TryGetProperty("error_code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.Number
                && codeElement.TryGetInt32(out var code)
                    ? code
                    : null;

            string? description = null;
            if (root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
            {
                description = descriptionElement.GetString();
                if (description is { Length: > MaxDescriptionLength })
                {
                    description = description[..MaxDescriptionLength];
                }
            }

            TimeSpan? retryAfter = null;
            long? migrateToChatId = null;
            if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                if (parameters.TryGetProperty("retry_after", out var retryElement)
                    && retryElement.ValueKind == JsonValueKind.Number
                    && retryElement.TryGetInt64(out var seconds)
                    && seconds >= 0)
                {
                    retryAfter = TimeSpan.FromSeconds(Math.Min(seconds, RetryAfterCeilingSeconds));
                }

                if (parameters.TryGetProperty("migrate_to_chat_id", out var migrateElement)
                    && migrateElement.ValueKind == JsonValueKind.Number
                    && migrateElement.TryGetInt64(out var newChatId))
                {
                    migrateToChatId = newChatId;
                }
            }

            return new TelegramApiResponse(ok, errorCode, description, retryAfter, migrateToChatId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
