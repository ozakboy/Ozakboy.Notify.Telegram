# Ozakboy.Notify.Telegram

給 .NET 10 用的 Telegram Bot 告警推播:訊息分級、節流、429 處理,而且 bot 權杖不會跑進你的日誌。

[English](README.md) | 繁體中文

```
dotnet add package Ozakboy.Notify.Telegram
```

需要 .NET 10。除 Microsoft 官方套件與 `Ozakboy.*`(`Ozakboy.Http`、`Ozakboy.Core.Abstractions`、`Ozakboy.Security`)之外沒有其他相依。

---

## 為什麼有這個套件

告警管道只有一個任務:出事的時候送得到。這排除了幾件常被做錯的事:

- **格式錯誤不能讓告警消失。**用 MarkdownV2 時,例外訊息裡一個沒跳脫的 `_` 就讓整則被拒。本套件只送純文字。
- **內容太長不能被拒收。**Telegram 單則上限 4096 字元。本套件依換行切分,絕不切斷 emoji 或組合字元。
- **一陣告警不能觸發 429。**同一個聊天每則間隔 1 秒、每分鐘有上限;真的收到 429,就照 Telegram 說的時間等。
- **Bot 權杖不能外洩。**它在網址路徑裡(`/bot<TOKEN>/sendMessage`),一般的遮罩看不到。見[權杖不進日誌](#權杖不進日誌)。

---

## 快速開始

```csharp
using Ozakboy.Notify.Telegram;

// 權杖來自環境變數(或下方的加密檔)。絕不寫在程式碼或 appsettings.json 裡。
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
            "交易所連線中斷,已暫停新單。",
            cancellationToken);

        if (result.IsFailure)
        {
            // 預期中的失敗不會擲出例外。依代碼分支,例如改寄 Email。
            Console.WriteLine(result.Error.Code);   // telegram.forbidden、telegram.rate_limited……
        }
    }
}
```

收到的訊息是 `[CRITICAL] 交易所連線中斷,已暫停新單。`

`AddTelegramNotifier` 在呼叫當下就驗證設定:權杖貼錯會在啟動時失敗,而不是等到第一則告警沒送到才發現。驗證訊息絕不回述權杖。

### 權杖放在加密檔

搭配 `Ozakboy.Security`,權杖可以用 Windows DPAPI 加密後存在磁碟上:

```csharp
using Ozakboy.Security.Protection;

ISecretProtector protector = new DpapiSecretProtector();
string stored = File.ReadAllText(tokenFilePath);   // 事先以 protector.Protect(token) 寫入

if (!protector.TryUnprotect(stored, out string? token))
{
    throw new InvalidOperationException("無法在這台電腦或這個帳號下解開 Telegram 權杖。");
}

services.AddTelegramNotifier(options =>
{
    options.BotToken = token;
    options.ChatId = chatId;
});
```

---

## 設定

| 設定 | 預設 | 說明 |
| --- | --- | --- |
| `BotToken` | 無 | 必填。會粗檢格式(`數字:祕密`),絕不回述。 |
| `ChatId` | 無 | 必填。整數(群組與頻道為負數)或 `@頻道`。 |
| `ApiBaseAddress` | `https://api.telegram.org/` | 只接受 `https`;`http` 限本機回送位址(自架 Bot API 伺服器)。 |
| `RequestTimeout` | 15 秒 | 單次 HTTP 請求。 |
| `MinimumInterval` | 1 秒 | 同一聊天兩則之間的間隔。 |
| `MaxMessagesPerMinute` | 20 | Telegram 的群組限制;私人聊天可以調高。 |
| `ThrottleWaitLimit` | 60 秒 | 在本地排隊最多等多久,超過回 `telegram.throttled`。 |
| `MaxRateLimitRetries` | 3 | 收到 429 後最多重送幾次。 |
| `MaxRetryAfter` | 60 秒 | 願意等的最長時間。對方要求更久就立刻回傳(上限 1 小時)。 |
| `TransientRetry` | 3 次、1 秒 → 10 秒 | 逾時、連線失敗、5xx。設 `RetryPolicy.NoRetry` 即為最多送達一次。 |
| `MaxPartsPerMessage` | 10 | 超過的內容截斷,最後一段註明截掉多少(上限 50)。 |

---

## 訊息

- **等級。**`Info` / `Warning` / `Critical` 顯示在最開頭:`[INFO]`、`[WARNING]`、`[CRITICAL]`。三級各對應一種反應:看看就好、今天處理、立刻處理。
- **Critical 優先。**排隊中的 `Critical` 永遠排在所有 `Info` / `Warning` 前面,下一個可用的節流額度就是它的,最多等一個 `MinimumInterval`,讓緊急告警能在 5 秒內送到。優先只影響排隊順序:`Critical` 仍要付每聊天間隔與每分鐘上限的額度,也要等 429 暫停結束,因為搶快只會換來 429,結果更晚送到。暫停結束後第一個出去的就是 `Critical`;已經在送的那一則不會被中斷。`Info` 與 `Warning` 之間依抵達順序。
- **純文字。**不設 `parse_mode`,每個字元都原樣顯示。什麼都不用跳脫。
- **切分。**超過 4096 個 UTF-16 字元就依換行切分,每段標上 `(1/3)`、`(2/3)`……。單行過長時切在字素邊界上,emoji、ZWJ 組合、組合符號都不會被拆開。分段依序送出;某段失敗就不送後面的,錯誤會帶 `partsSent` / `partsTotal`。
- **關閉連結預覽。**預覽會讓 Telegram 的伺服器去抓告警裡的任何網址。

---

## 429、重試與重複

`sendMessage` 不是冪等的,Telegram 也沒有去重用的識別碼。本套件依每種失敗「代表送達了沒有」分別處理:

| 失敗 | 送達了嗎 | 處置 |
| --- | --- | --- |
| 429 | 沒有,Telegram 拒收 | 等 `parameters.retry_after`(或 `Retry-After` 標頭)後重送,不可能重複。暫停期對整個通知器生效。 |
| 429 要求等超過 `MaxRetryAfter` | 沒有 | 立刻回傳 `telegram.rate_limited`,附 `retryAfterSeconds`。暫停結束前,之後的送出直接失敗。 |
| 逾時、連線失敗、5xx | 不知道 | 依 `TransientRetry` 重送(至少送達一次)。 |
| 400 / 401 / 403 / 404 | 沒有 | 立刻回傳,重送也不會變。 |
| 2xx 但沒有 `"ok": true` | 不知道 | 回傳 `telegram.unexpected_response`,不重送。通常是代理伺服器在回話。 |

**為什麼預設至少送達一次。**告警情境下兩種錯誤的代價不對等:重複一則,收件人多看一眼;漏掉一則,可能沒人知道系統已經停止交易。若你的情況是重複比漏送更貴,設 `TransientRetry = RetryPolicy.NoRetry`。

本套件已經替你重試過的失敗會以 `ErrorCategory.Exhausted` 回傳,不是暫時性的,這樣你自己的重試層不會再把次數乘上去。

**已知限制。**節流用的是固定時間窗:跨越時間窗邊界時,偶爾會有兩則間隔不到 1 秒。Telegram 的每聊天限制有彈性,偶發的 429 由上面的重送機制吸收。本分鐘的配額用完時,`Critical` 也要等到下一分鐘。

**與 `Ozakboy.Http` 的分工。**具名用戶端以 `AddOzakboyHttpPipeline`(`Ozakboy.Http` 0.3.0)組成,由它提供遮罩日誌、權杖遮罩器與逾時;`HttpPipelineClient` 把例外收斂為 `Result`,並分辨逾時與取消;節流用 `WeightedRateLimiter`(建在 `System.Threading.RateLimiting` 上、以 `TimeProvider` 驅動),放在通知器自己的優先閘門裡。管線的限流段關閉,`RetryHandler` 設為 `RetryPolicy.NoRetry`。重試收在 `TelegramNotifier` 的單一迴圈裡,理由有三:`RetryHandler` 讀不到本體裡的 `retry_after`;要讓它重試,`sendMessage` 就得被謊報為冪等;而管線的限流器依抵達順序放行,給不了 `Critical` 優先,也無法讓 429 暫停由整個通知器共用。每一次送出(含重送)都重新在閘門排隊、各付一份額度,與 0.3.0 管線的原則相同。

---

## 權杖不進日誌

位址是 `https://api.telegram.org/bot<TOKEN>/sendMessage`,任何會印出請求位址的地方都會印出權杖。權杖是路徑的一段,query 參數遮罩對它無效。本套件逐一處理位址可能露出的地方:

| 位址可能露出的地方 | 本套件的處置 |
| --- | --- |
| `IHttpClientFactory` 預設的 `LogicalHandler` / `ClientHandler` 日誌:在 Information 層級寫出完整位址,還放進 scope;.NET 只遮 query。 | `AddOzakboyHttpPipeline` 會對這個具名用戶端移除它們。 |
| `Ozakboy.Http` 的 `SanitizingLoggingHandler`(管線的一段):記錄位址,失敗時也記錄例外。 | 權杖與冒號後的祕密段登記在 `HttpPipelineOptions.KnownSecrets`,記下的位址與例外文字都經過遮罩,交給日誌器的是 `SanitizedException` 而不是原始例外。本體日誌保持關閉,因為本體裡是 chat id 與訊息內容。`TelegramNotifier` 自己的日誌只記等級、第幾段、錯誤代碼與耗時。 |
| 由傳輸層例外組出的錯誤訊息,以及 `Error.Exception`。 | 門面以 `CreateOzakboyHttpPipelineClient` 建立,用同一個遮罩器遮罩;每個回傳的錯誤在通知器出口再遮一次:代碼、訊息、每一筆資料。例外一律換成 `RedactedException`,保留遮罩後的 `ToString()`,不帶內層例外。 |
| 回應本體的回聲。管線不會遮它:4xx 對管線而言是成功取得回應。 | 錯誤只帶 Telegram 的 `description`(已截斷、已遮罩),不帶本體。通知器在管線遮罩器之外仍保留自己的出口遮罩,就是為了這一類。 |

遮罩同時登記完整權杖與冒號後的段落:冒號可能被百分比編碼,祕密段則不受任何編碼影響。

測試裡有一條外洩測試:用一把可辨識的假權杖跑完整注入流程,涵蓋每一種情境(成功、400/401/403、429、5xx、逾時、訊息裡直接帶完整位址的傳輸層例外),然後檢查所有日誌表面與所有錯誤欄位。

**不在本套件控制範圍內,請自行留意:**

- **你自己加的處理器。**不要在 `Ozakboy.Notify.Telegram` 用戶端上掛會記錄位址的處理器,也要留意 `ConfigureHttpClientDefaults` 全域加上的那些。
- **分散式追蹤。**掛了 `ActivityListener` 或 OpenTelemetry 時,`System.Net.Http` 會記下 `url.full`,裡面有路徑。
- **事件追蹤。**對 `System-Net-Http` 開 `EventListener` 或 ETW 工作階段時,`RequestStart` 事件帶有路徑。
- **`HttpClient` 建構式。**`new TelegramNotifier(httpClient, …)` 會照原樣使用你的用戶端。它若來自 `IHttpClientFactory`,請自行移除預設日誌。

---

## 錯誤

所有預期中的失敗都是 `Result` 失敗。只有程式缺陷(設定不合法、`null`、釋放後使用)才擲出例外。

| 代碼 | 分類 | 意思 |
| --- | --- | --- |
| `telegram.unauthorized` | Unauthorized | 401:權杖無效或已撤銷。 |
| `telegram.forbidden` | Forbidden | 403:bot 被踢、被封鎖,或無權發言。 |
| `telegram.chat_not_found` | NotFound | 400 `chat not found`。 |
| `telegram.chat_migrated` | NotFound | 群組升級為超級群組;新 id 在 `migrateToChatId`。 |
| `telegram.bad_request` | Validation | 其他 400。 |
| `telegram.not_found` | NotFound | 404:通常是 `ApiBaseAddress` 設錯。 |
| `telegram.rate_limited` | RateLimited / Exhausted | 429,或暫停期尚未結束。可能帶 `retryAfterSeconds`。 |
| `telegram.unavailable` | Unavailable / Exhausted | 5xx。 |
| `telegram.timeout` | Timeout / Exhausted | 請求逾時。 |
| `telegram.network` | Network / Exhausted | DNS、TCP、TLS 或連線中斷。 |
| `telegram.cancelled` | Cancelled | 你的 `CancellationToken` 觸發了。 |
| `telegram.throttled` | RateLimited | 本地節流排隊超過 `ThrottleWaitLimit`,沒有送出。 |
| `telegram.unexpected_response` | Unexpected | 回應無法確認已送達。 |

資料鍵列在 `TelegramErrorDataKeys`:`statusCode`、`telegramErrorCode`、`description`、`retryAfterSeconds`、`migrateToChatId`、`attempts`、`partsSent`、`partsTotal`、`level`、`transportErrorCode`。

---

## 測試

- **你的程式。**相依 `ITelegramNotifier`(只有一個方法),換成假實作即可,不需要權杖。
- **本套件。**MSTest 跑在 Microsoft.Testing.Platform 上,搭配自寫的 `HttpMessageHandler` 與 `FakeTimeProvider`。不連網、不用模擬框架。
- **實機測試。**`[TestCategory("TelegramLive")]` 只在 `TELEGRAM_BOT_TOKEN` 與 `TELEGRAM_CHAT_ID` 都有設定時才真的送,否則以 `Inconclusive` 收場。不讀任何設定檔。

```
dotnet test -c Release
```

---

## 授權

MIT
