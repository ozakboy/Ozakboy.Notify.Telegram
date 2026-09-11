# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-09-12

First release. Targets `net10.0`. Depends only on first-party Microsoft packages and `Ozakboy.Http` 0.2.0,
`Ozakboy.Core.Abstractions` 0.3.0, and `Ozakboy.Security` 0.1.1. Replaces `Telegram.Bot` for alerting.

首次發佈。目標框架 `net10.0`,只相依 Microsoft 官方套件與 `Ozakboy.Http` 0.2.0、`Ozakboy.Core.Abstractions` 0.3.0、
`Ozakboy.Security` 0.1.1。用於告警推播,取代 `Telegram.Bot`。

### Added

- **`ITelegramNotifier` / `TelegramNotifier`**: `SendAsync(TelegramNotification, CancellationToken)` returns a
  `Result`, and expected failures never throw. The interface has a single method, so callers' tests can substitute a fake.
  以 `Result` 回報結果、預期中的失敗不擲例外;介面只有一個方法,方便呼叫端測試替換。
- **Levels**: `NotificationLevel.Info` / `Warning` / `Critical`, shown first as `[INFO]` / `[WARNING]` /
  `[CRITICAL]`. Undefined levels are rejected at construction rather than guessed.
  三級通知,標記顯示在最開頭;未定義的等級在建立時就拒絕,不猜。
- **Plain text only**: no `parse_mode`, so no character can get a message rejected.
  只送純文字,不會因為任何字元被拒收。
- **Splitting**: content over 4096 UTF-16 code units is split on line breaks. Each part is marked `(n/N)`. An over-long line is cut
  on grapheme boundaries, so surrogate pairs, ZWJ sequences, and combining marks stay whole. A `MaxPartsPerMessage` cap
  truncates runaway content and notes how much was dropped.
  超長內容依行切分,行內切在字素邊界;另設分段上限,截斷時註明截掉多少。
- **Throttling**: `WeightedRateLimiter` (on `System.Threading.RateLimiting`, driven by `TimeProvider`) spaces
  messages to the chat by `MinimumInterval` (1 s) and caps `MaxMessagesPerMinute` (20). Resends pay for a slot too.
  每聊天間隔與每分鐘上限兩個桶;重送也要付節流額度。
- **429 handling**: waits `parameters.retry_after` from the body, falling back to the `Retry-After` header, then
  resends. The pause is shared across the notifier. A wait longer than `MaxRetryAfter` returns
  `telegram.rate_limited` at once, and later sends fail fast until it ends.
  依本體的 `retry_after` 等待後重送;暫停期全通知器共用;要求等太久就立刻交回呼叫端。
- **At-least-once for transient failures**: timeouts, connection failures, and 5xx are resent per `TransientRetry`.
  This is the default because a duplicated alert costs less than a lost one. `RetryPolicy.NoRetry` gives at-most-once. Failures already
  retried come back as `ErrorCategory.Exhausted`.
  暫時性失敗預設「至少送達一次」;已重試過的失敗標為 `Exhausted`。
- **Error codes** in `TelegramErrorCodes` for 401, 403, `chat not found`, chat migration (with `migrateToChatId`), other 400s, 404,
  429, 5xx, timeout, network, cancellation, local throttling, and unconfirmed delivery. **Data keys** are in `TelegramErrorDataKeys`.
  各種失敗皆有可辨識的錯誤代碼,暫時性判斷正確(401 / 403 不是暫時性)。
- **`AddTelegramNotifier(options => …)`**: validates at registration. It registers a named `HttpClient` with an
  infinite `HttpClient.Timeout` and with `IHttpClientFactory`'s default logging removed.
  註冊時即驗證設定;具名用戶端移除 factory 預設日誌。
- **`TelegramNotifierOptions.Validate()`**: a token shape check and a chat-id check, with https-only base addresses (loopback http
  allowed). Messages never echo the token. There is no default token.
  權杖格式粗檢、chat id 檢查、只允許 https;訊息絕不回述權杖;沒有預設權杖。

### Security

- The bot token sits in the URL path, where query-parameter masking cannot reach it. The notifier removes the
  factory's default request logging, never logs request URIs, the chat id, or message text, and redacts every returned
  error: code, message, and each data entry. Exceptions are always replaced by `RedactedException`, which keeps a
  redacted `ToString()` and holds no inner exception. Both the full token and its post-colon secret are registered for
  redaction. A credential-leak test covers success, 400/401/403, 429, 5xx, timeouts, and transport exceptions
  whose messages contain the full URI.
  權杖位於網址路徑。本套件移除預設日誌、不記錄位址、遮罩所有回傳錯誤,例外一律換成不帶內層例外的替身;
  外洩測試涵蓋所有失敗路徑,包括訊息裡直接帶完整位址的例外。
