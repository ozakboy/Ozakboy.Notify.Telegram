# Ozakboy.Notify.Telegram

Telegram Bot alerting for .NET 10: levelled messages, throttling, 429 handling, and a bot token that never ends up in your logs.

English | [繁體中文](README_zh-TW.md)

```
dotnet add package Ozakboy.Notify.Telegram
```

Requires .NET 10. No dependencies beyond first-party Microsoft packages and `Ozakboy.*` (`Ozakboy.Http`, `Ozakboy.Core.Abstractions`, `Ozakboy.Security`).

---

## Why this exists

An alert channel has one job: to get through when something has gone wrong. That rules out a few things people
usually get wrong:

- **A formatting mistake must not lose the alert.** With MarkdownV2, one unescaped `_` in an exception message gets
  the whole message rejected. This package sends plain text only.
- **Long content must not be rejected.** Telegram caps a message at 4096 characters. This package splits along line
  breaks and never cuts inside an emoji or a combining sequence.
- **A burst of alerts must not trigger a 429.** Messages to one chat are spaced one second apart and capped per
  minute. When a 429 happens anyway, the package waits exactly as long as Telegram asked.
- **The bot token must not leak.** It sits in the URL path (`/bot<TOKEN>/sendMessage`), where the usual masking
  cannot see it. See [Keeping the token out of logs](#keeping-the-token-out-of-logs).

---

## Quick start

```csharp
using Ozakboy.Notify.Telegram;

// The token comes from the environment (or an encrypted file, below). Never from source or appsettings.json.
services.AddTelegramNotifier(options =>
{
    options.BotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    options.ChatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
});
```

```csharp
public sealed class Watchdog(ITelegramNotifier telegram)
{
    public async Task OnConnectionLostAsync(CancellationToken cancellationToken)
    {
        var result = await telegram.SendAsync(
            NotificationLevel.Critical,
            "Exchange connection lost. New orders are paused.",
            cancellationToken);

        if (result.IsFailure)
        {
            // Nothing here throws for expected failures. Branch on the code, e.g. fall back to e-mail.
            Console.WriteLine(result.Error.Code);   // telegram.forbidden, telegram.rate_limited, ...
        }
    }
}
```

The message arrives as `[CRITICAL] Exchange connection lost. New orders are paused.`

`AddTelegramNotifier` validates the options when you call it, so a mistyped token fails at start-up rather than when
the first alert goes missing. Validation messages never echo the token.

### Keeping the token in an encrypted file

With `Ozakboy.Security`, the token can live on disk encrypted with Windows DPAPI:

```csharp
using Ozakboy.Security.Protection;

ISecretProtector protector = new DpapiSecretProtector();
string stored = File.ReadAllText(tokenFilePath);   // written earlier with protector.Protect(token)

if (!protector.TryUnprotect(stored, out string? token))
{
    throw new InvalidOperationException("The Telegram token cannot be decrypted on this machine or account.");
}

services.AddTelegramNotifier(options =>
{
    options.BotToken = token;
    options.ChatId = chatId;
});
```

---

## Options

| Option | Default | Notes |
| --- | --- | --- |
| `BotToken` | none | Required. Format-checked (`digits:secret`), never echoed. |
| `ChatId` | none | Required. An integer (negative for groups and channels) or `@channel`. |
| `ApiBaseAddress` | `https://api.telegram.org/` | `https` only; `http` is accepted for loopback (a self-hosted Bot API server). |
| `RequestTimeout` | 15 s | Per HTTP request. |
| `MinimumInterval` | 1 s | Spacing between messages to the chat. |
| `MaxMessagesPerMinute` | 20 | Telegram's group limit. A private chat can go higher. |
| `ThrottleWaitLimit` | 60 s | How long a message may queue locally before giving up with `telegram.throttled`. |
| `MaxRateLimitRetries` | 3 | Resends after a 429. |
| `MaxRetryAfter` | 60 s | The longest wait honoured. If Telegram asks for longer, the send returns at once (max 1 hour). |
| `TransientRetry` | 3 attempts, 1 s → 10 s | For timeouts, connection failures, and 5xx. `RetryPolicy.NoRetry` for at-most-once. |
| `MaxPartsPerMessage` | 10 | Longer content is truncated, and the last part says how much was cut (max 50). |

---

## Messages

- **Levels.** `Info`, `Warning`, and `Critical` appear first as `[INFO]`, `[WARNING]`, and `[CRITICAL]`. With three levels,
  each one calls for a different reaction: glance, handle today, handle now. The level does not change how a message is sent. `Critical` does not
  skip the throttle, because skipping it earns a 429 and arrives later.
- **Plain text.** No `parse_mode`. Every character shows as written. Do not escape anything.
- **Splitting.** Content over 4096 UTF-16 code units is split on line breaks. Each part is marked `(1/3)`, `(2/3)`, and so on. A single line
  that is too long is cut on a grapheme boundary, so emoji, ZWJ sequences, and combining marks stay whole. Parts go
  out in order. If one fails, the rest are not sent, and the error reports `partsSent` / `partsTotal`.
- **Link previews are off.** A preview would have Telegram's servers fetch any URL in your alert.

---

## 429s, retries, and duplicates

`sendMessage` is not idempotent, and Telegram has no deduplication key. The package treats each kind of failure
according to what it says about delivery:

| Failure | Delivered? | What happens |
| --- | --- | --- |
| 429 | No, Telegram refused it | Wait `parameters.retry_after` (or the `Retry-After` header), then resend. This cannot duplicate. The pause applies to every send on the notifier. |
| 429 asking for more than `MaxRetryAfter` | No | Return `telegram.rate_limited` at once with `retryAfterSeconds`. Later sends fail fast until the pause ends. |
| Timeout, connection failure, 5xx | Unknown | Resend per `TransientRetry` (at-least-once). |
| 400, 401, 403, 404 | No | Return at once. Resending changes nothing. |
| 2xx without `"ok": true` | Unknown | Return `telegram.unexpected_response` without resending. It usually means a proxy answered. |

**Why at-least-once by default.** For alerts, the two mistakes do not cost the same. A duplicate costs the reader a
second glance. A lost alert can mean nobody knows the system has stopped trading. If duplicates cost you more, set
`TransientRetry = RetryPolicy.NoRetry`.

Failures the package has already retried come back as `ErrorCategory.Exhausted`, which is not transient. That keeps your own
retry layer from multiplying the attempts.

**Division of labour with `Ozakboy.Http`.** This package uses `HttpPipelineClient`, which folds exceptions into `Result`
and tells timeouts from cancellation, and `WeightedRateLimiter`, which is built on `System.Threading.RateLimiting` and runs on `TimeProvider`.
It does not use `RetryHandler`, for three reasons. `RetryHandler` cannot read the body's `retry_after`. `sendMessage` would have to be falsely labelled
idempotent before `RetryHandler` retried it. And in the standard order its retries would bypass the throttle. So retries live in one loop in
`TelegramNotifier`.

---

## Keeping the token out of logs

The address is `https://api.telegram.org/bot<TOKEN>/sendMessage`. Anything that prints the request URI prints the
token. Query-parameter masking cannot help, because the token is a path segment. The package handles each
place the URI can surface:

| Where the URI can surface | What the package does |
| --- | --- |
| `IHttpClientFactory`'s default `LogicalHandler` / `ClientHandler` logging. It logs the full URI at Information level, puts it in a scope, and .NET redacts only the query. | `RemoveAllLoggers()` on the named client. |
| `Ozakboy.Http`'s `SanitizingLoggingHandler`. It masks only query values and passes the raw exception to the logger. | Not used on this client. `TelegramNotifier` logs only the level, part number, error code, and timing. It never logs the URI, the chat id, or the text. |
| Error messages built from transport exceptions, and `Error.Exception`. | Every returned error is redacted: the code, the message, and every data entry. The exception is always replaced by `RedactedException`, which keeps a redacted `ToString()` and holds no inner exception. |
| Response bodies echoing content. | Errors carry only Telegram's `description` (truncated and redacted), never the body. |

Redaction registers both the full token and the part after the colon. The colon may be percent-encoded, but the
secret part never changes under encoding.

The test suite includes a leak test. It runs a recognisable fake token through the full injected pipeline in every
scenario: success, 400/401/403, 429, 5xx, timeout, and transport exceptions whose messages contain the full URI. It then checks every
log surface and every error field.

**Not covered by the package. Keep these in mind:**

- **Handlers you add yourself.** Do not attach URI-logging handlers to the `Ozakboy.Notify.Telegram` client, and watch
  for ones added globally through `ConfigureHttpClientDefaults`.
- **Distributed tracing.** When an `ActivityListener` or OpenTelemetry is attached, `System.Net.Http` records `url.full`,
  which includes the path.
- **Event tracing.** An `EventListener` or ETW session on `System-Net-Http` sees `RequestStart` with the path.
- **The `HttpClient` constructor.** `new TelegramNotifier(httpClient, …)` uses your client as is. If it came from
  `IHttpClientFactory`, remove its default loggers yourself.

---

## Errors

Every expected failure is a `Result` failure. Nothing throws except programming errors, such as invalid options, `null`, or use after
dispose.

| Code | Category | Meaning |
| --- | --- | --- |
| `telegram.unauthorized` | Unauthorized | 401: token invalid or revoked. |
| `telegram.forbidden` | Forbidden | 403: the bot was kicked, blocked, or cannot post. |
| `telegram.chat_not_found` | NotFound | 400 `chat not found`. |
| `telegram.chat_migrated` | NotFound | The group became a supergroup. The new id is in `migrateToChatId`. |
| `telegram.bad_request` | Validation | Any other 400. |
| `telegram.not_found` | NotFound | 404: usually a wrong `ApiBaseAddress`. |
| `telegram.rate_limited` | RateLimited / Exhausted | 429, or a pause still running. May carry `retryAfterSeconds`. |
| `telegram.unavailable` | Unavailable / Exhausted | 5xx. |
| `telegram.timeout` | Timeout / Exhausted | A request timed out. |
| `telegram.network` | Network / Exhausted | DNS, TCP, TLS, or a dropped connection. |
| `telegram.cancelled` | Cancelled | Your `CancellationToken` fired. |
| `telegram.throttled` | RateLimited | Local throttling queue full for longer than `ThrottleWaitLimit`. Nothing was sent. |
| `telegram.unexpected_response` | Unexpected | The response does not confirm delivery. |

Data keys are listed in `TelegramErrorDataKeys`: `statusCode`, `telegramErrorCode`, `description`, `retryAfterSeconds`,
`migrateToChatId`, `attempts`, `partsSent`, `partsTotal`, `level`, and `transportErrorCode`.

---

## Testing

- **Your code.** Depend on `ITelegramNotifier`, which has a single method, and substitute a fake. No token is needed.
- **This package.** MSTest on Microsoft.Testing.Platform, with a hand-written `HttpMessageHandler` and
  `FakeTimeProvider`. No network, no mocking framework.
- **Live test.** `[TestCategory("TelegramLive")]` sends a real message only when both `TELEGRAM_BOT_TOKEN` and
  `TELEGRAM_CHAT_ID` are set. Otherwise it ends `Inconclusive`. It reads nothing from configuration files.

```
dotnet test -c Release
```

---

## Licence

MIT
