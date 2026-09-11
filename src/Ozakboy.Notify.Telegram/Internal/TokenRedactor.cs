using Ozakboy.Core.Abstractions;
using Ozakboy.Security.Masking;

namespace Ozakboy.Notify.Telegram.Internal;

/// <summary>
/// 把權杖從任何要交出去的東西上遮掉:字串、錯誤(代碼、訊息、資料)與例外。
/// Masks the token out of anything about to leave: strings, errors (code, message, data), and exceptions.
/// </summary>
/// <remarks>
/// <para>
/// 權杖在請求位址的路徑段裡,沒有「欄位名」可以判斷,<see cref="SecretMasker"/> 的欄位名規則幫不上忙;
/// 唯一攔得住的是「已登記祕密的字面替換」,不管它出現在哪個位置都會被換掉。
/// The token sits in a path segment of the request URI, where there is no field name to judge by, so
/// <see cref="SecretMasker"/>'s name rules cannot help. Only literal replacement of a registered secret catches
/// it, wherever it turns up.
/// </para>
/// <para>
/// 同時登記完整權杖與冒號後面的祕密段:冒號在某些位址序列化形式裡會被百分比編碼成 <c>%3A</c>,
/// 那時完整權杖的字面比對就失效了;祕密段只含英數字、<c>_</c> 與 <c>-</c>,任何編碼都不會改變它。
/// 冒號前的 bot id 不是祕密,單獨出現不需要遮。
/// Both the full token and the secret half after the colon are registered: some URI serialisations
/// percent-encode the colon as <c>%3A</c>, which defeats a literal match on the full token, while the secret
/// half contains only letters, digits, <c>_</c> and <c>-</c>, which no encoding alters. The bot id before the
/// colon is not a secret and needs no masking on its own.
/// </para>
/// </remarks>
internal sealed class TokenRedactor
{
    private readonly SecretMasker _masker = new();

    public TokenRedactor(string botToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);

        _masker.RegisterKnownSecret(botToken);

        var colon = botToken.IndexOf(':', StringComparison.Ordinal);
        var secretPart = colon >= 0 ? botToken[(colon + 1)..] : string.Empty;
        if (secretPart.Length >= SecretMasker.MinimumKnownSecretLength)
        {
            _masker.RegisterKnownSecret(secretPart);
        }
    }

    public string Redact(string? text) => _masker.MaskText(text) ?? string.Empty;

    /// <summary>
    /// 回傳遮罩後的新錯誤。例外一律換成 <see cref="RedactedException"/>,不論原始例外看起來乾不乾淨。
    /// Returns a redacted copy of the error. The exception is always swapped for a
    /// <see cref="RedactedException"/>, however clean the original looks.
    /// </summary>
    /// <remarks>
    /// 「看起來乾淨就保留原物件」是個誘人的最佳化,但它把安全建立在「現在的檢查涵蓋了例外物件的所有欄位」上 ——
    /// 內層例外的 <c>Data</c>、自訂例外的屬性,日誌框架都可能結構化輸出。一律替換才是失敗封閉。
    /// Keeping the original when it looks clean is a tempting optimisation, but it rests safety on the check
    /// covering every field of the exception object — an inner exception's <c>Data</c> or a custom exception's
    /// properties may all be rendered structurally by a logging framework. Always replacing is what fails closed.
    /// </remarks>
    public Error Redact(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Error(Redact(error.Code), Redact(error.Message), error.Category)
        {
            Exception = error.Exception is null ? null : Redact(error.Exception),
            Data = RedactData(error.Data),
        };
    }

    public RedactedException Redact(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is RedactedException already)
        {
            return already;
        }

        return new RedactedException(
            exception.GetType().FullName ?? exception.GetType().Name,
            Redact(exception.Message),
            Redact(exception.ToString()));
    }

    private Dictionary<string, string>? RedactData(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null)
        {
            return null;
        }

        var redacted = new Dictionary<string, string>(data.Count, StringComparer.Ordinal);
        foreach (var entry in data)
        {
            redacted[Redact(entry.Key)] = Redact(entry.Value);
        }

        return redacted;
    }
}
