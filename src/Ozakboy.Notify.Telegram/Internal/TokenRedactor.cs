using Ozakboy.Core.Abstractions;
using Ozakboy.Http;
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
/// 冒號前的 bot id 不是祕密,單獨出現不需要遮。這份清單(<see cref="SecretLiterals"/>)也是註冊時登記進
/// <c>Ozakboy.Http</c> 管線 <see cref="HttpPipelineOptions.KnownSecrets"/> 的那一份,兩邊不會各算各的。
/// Both the full token and the secret half after the colon are registered: some URI serialisations
/// percent-encode the colon as <c>%3A</c>, which defeats a literal match on the full token, while the secret
/// half contains only letters, digits, <c>_</c> and <c>-</c>, which no encoding alters. The bot id before the
/// colon is not a secret and needs no masking on its own. This list (<see cref="SecretLiterals"/>) is also the one
/// registered in the <c>Ozakboy.Http</c> pipeline's <see cref="HttpPipelineOptions.KnownSecrets"/>, so the two
/// sides never disagree about what the token looks like.
/// </para>
/// <para>
/// <b>管線已經會遮罩,為什麼還留著這一道。</b><c>Ozakboy.Http</c> 0.3.0 的管線會用登記了權杖的遮罩器處理它自己經手的東西:
/// 日誌處理器記下的位址與例外、門面回傳的錯誤(例外換成 <see cref="SanitizedException"/>)。這一道守的則是通知器的出口,
/// 那裡還有管線從來看不到的來源:Telegram 回應本體裡的 <c>description</c> —— 4xx 對門面而言是「成功取得回應」,
/// 本體由通知器自己解析,不經管線遮罩;以及通知器自己組出的錯誤(讀本體失敗、排隊或退避時被取消,取消錯誤帶著原始例外)。
/// 它也保證本套件回傳的 <c>Error.Exception</c> 只會是 <see cref="RedactedException"/>,不論下層交來的是什麼 —— 失敗封閉。
/// <b>Why this stays even though the pipeline masks too.</b> The <c>Ozakboy.Http</c> 0.3.0 pipeline masks what it
/// handles itself with a masker that knows the token: the URI and exceptions its logging handler records, and the
/// errors its facade returns (with exceptions swapped for <see cref="SanitizedException"/>). This class guards the
/// notifier's exit, where sources the pipeline never sees also arrive: the <c>description</c> in Telegram's response
/// body — a 4xx is a successfully obtained response as far as the facade is concerned, and the notifier parses the
/// body itself, outside the pipeline's masking — and errors the notifier builds on its own (a failed body read, a
/// cancellation while queued or backing off, which carries the original exception). It also guarantees that every
/// <c>Error.Exception</c> this package returns is a <see cref="RedactedException"/>, whatever the lower layer handed
/// over: it fails closed.
/// </para>
/// </remarks>
internal sealed class TokenRedactor
{
    private readonly SecretMasker _masker = new();

    public TokenRedactor(string botToken)
    {
        foreach (var secret in SecretLiterals(botToken))
        {
            _masker.RegisterKnownSecret(secret);
        }
    }

    /// <summary>
    /// 只登記了這把權杖的遮罩器。手動建立門面(不經 <c>AddOzakboyHttpPipeline</c> 的建構式)時交給
    /// <see cref="HttpPipelineClient"/>,讓門面回傳的錯誤在離開 <c>Ozakboy.Http</c> 之前就已遮掉權杖。
    /// The masker carrying only this token. Handed to <see cref="HttpPipelineClient"/> when the facade is built by
    /// hand (the constructors that bypass <c>AddOzakboyHttpPipeline</c>), so errors from the facade already have the
    /// token masked before they leave <c>Ozakboy.Http</c>.
    /// </summary>
    public SecretMasker Masker => _masker;

    /// <summary>
    /// 代表這把權杖、需要登記為已知祕密的字面值:完整權杖,以及長度足夠時冒號後的祕密段。
    /// The literals that stand for this token and must be registered as known secrets: the full token, and the
    /// secret half after the colon when it is long enough.
    /// </summary>
    /// <param name="botToken">Bot 權杖。The bot token.</param>
    /// <returns>要登記的字面值。The literals to register.</returns>
    public static IReadOnlyList<string> SecretLiterals(string botToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);

        var colon = botToken.IndexOf(':', StringComparison.Ordinal);
        var secretPart = colon >= 0 ? botToken[(colon + 1)..] : string.Empty;
        return secretPart.Length >= SecretMasker.MinimumKnownSecretLength ? [botToken, secretPart] : [botToken];
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

        // Ozakboy.Http 交來的已經是它的替身:沿用替身保存的原始型別與完整細節,再以這把權杖遮一次。
        // 直接拿替身本身當來源,原始型別會變成替身自己的型別名稱,診斷時就看不出原本是連線失敗還是別的。
        // What Ozakboy.Http hands over is already its stand-in: carry over the original type and full details it
        // kept, masked once more against this token. Taking the stand-in itself as the source would record the
        // stand-in's own type name, hiding whether this was a connection failure or something else.
        if (exception is SanitizedException sanitized)
        {
            return new RedactedException(
                sanitized.OriginalExceptionType ?? sanitized.GetType().FullName ?? nameof(SanitizedException),
                Redact(sanitized.Message),
                Redact(sanitized.SanitizedDetails ?? sanitized.ToString()));
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
