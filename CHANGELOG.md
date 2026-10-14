# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-10-15

First release. Targets `net10.0`. Depends only on first-party Microsoft packages and `Ozakboy.Http` 0.3.2,
`Ozakboy.Core.Abstractions` 0.3.0, and `Ozakboy.Security` 0.1.1. Replaces `Telegram.Bot` for alerting.

首次發佈。目標框架 `net10.0`,只相依 Microsoft 官方套件與 `Ozakboy.Http` 0.3.2、`Ozakboy.Core.Abstractions` 0.3.0、
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
- **Critical priority lane**: a queued `Critical` is always ahead of every queued `Info` and `Warning`. It takes the next free
  throttling slot, at most one `MinimumInterval` away, so critical alerts can arrive within five seconds (FR-12).
  Priority changes the queue order only. `Critical` still honours the per-chat and per-minute limits and waits out
  429 pauses, and the first message out after a pause is the `Critical`. A message already being sent is not
  interrupted. `Info` and `Warning` keep their arrival order. It is a small two-queue gate in front of the limiter, BCL only.
  Critical 優先通道:永遠排在 Info / Warning 前面、下一個節流額度就送出;只影響排隊順序,不繞過速率限制,
  429 暫停結束後第一個送出的是 Critical。
- **Known limitation**: the throttle uses fixed windows, so two messages can occasionally go out less than a second apart across a
  window boundary. An occasional 429 is absorbed by the resend logic.
  已知限制:固定時間窗在邊界可能讓兩則間隔不到 1 秒,偶發的 429 由重送吸收。
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
- **`AddTelegramNotifier(options => …)`**: validates at registration. It builds the named `HttpClient` with
  `Ozakboy.Http`'s `AddOzakboyHttpPipeline`, which sets an infinite `HttpClient.Timeout` and removes
  `IHttpClientFactory`'s default logging.
  註冊時即驗證設定;具名用戶端以 `AddOzakboyHttpPipeline` 組成,由它移除 factory 預設日誌。
- **`TelegramNotifierOptions.Validate()`**: a token shape check and a chat-id check, with https-only base addresses (loopback http
  allowed). Messages never echo the token. There is no default token.
  權杖格式粗檢、chat id 檢查、只允許 https;訊息絕不回述權杖;沒有預設權杖。

### Changed

- **Technical: built on `Ozakboy.Http` 0.3.2.** The named client is now assembled with `AddOzakboyHttpPipeline`, with
  signing and rate limiting off and its retry handler set to `RetryPolicy.NoRetry`. The token and its post-colon
  secret go into `HttpPipelineOptions.KnownSecrets`, and the facade comes from `CreateOzakboyHttpPipelineClient`. This
  replaces the package's own `RemoveAllLoggers()` and infinite-timeout setup and its hand-built facade. The pipeline's
  masked logging handler now runs on this client and logs the URI with the token masked. The exit redaction
  (`RedactedException`) stays, for what the pipeline never sees: Telegram's `description` and errors the notifier
  builds itself. The priority gate and the resend loop are unchanged. The rules the 0.3.0 pipeline introduced (every
  attempt pays its own weight, local rate-limit timeouts are not retried, queueing is not attempt time) already held
  for them.
  技術改進:改用 `Ozakboy.Http` 0.3.2。具名用戶端改以 `AddOzakboyHttpPipeline` 組成(簽章、限流關閉,重試設為 `NoRetry`),
  權杖登記進 `KnownSecrets`,門面改由 `CreateOzakboyHttpPipelineClient` 建立,取代本套件自行移除預設日誌、自設逾時與手動建立門面的做法;
  管線的遮罩日誌處理器現在會記下已遮罩權杖的位址。出口遮罩(`RedactedException`)保留,負責管線看不到的來源。
  優先閘門與重送迴圈不變:0.3.0 管線的新原則(每次嘗試各付權重、本地限流逾時不重試、排隊不計入單次逾時)在這裡本來就成立。

### Security

- The bot token sits in the URL path, where query-parameter masking cannot reach it. The pipeline removes the
  factory's default request logging and masks the token in the URIs and exceptions it logs, because the token is
  registered on the client's masker. The notifier never logs the chat id or message text, and it redacts every returned
  error again at its exit: code, message, and each data entry. Exceptions are always replaced by `RedactedException`,
  which keeps a redacted `ToString()` and holds no inner exception. Both the full token and its post-colon secret are
  registered for redaction. A credential-leak test covers success, 400/401/403, 429, 5xx, timeouts, and transport
  exceptions whose messages contain the full URI.
  權杖位於網址路徑。管線移除預設日誌,並以登記了權杖的遮罩器記錄位址與例外;通知器不記 chat id 與訊息內容,
  所有回傳錯誤在出口再遮一次,例外一律換成不帶內層例外的替身;外洩測試涵蓋所有失敗路徑,包括訊息裡直接帶完整位址的例外。
