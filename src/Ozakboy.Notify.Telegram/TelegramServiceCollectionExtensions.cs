using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Ozakboy.Core.Abstractions;
using Ozakboy.Http;
using Ozakboy.Notify.Telegram.Internal;

namespace Ozakboy.Notify.Telegram;

/// <summary>
/// 把 <see cref="TelegramNotifier"/> 註冊進相依性注入容器。
/// Registers <see cref="TelegramNotifier"/> with the dependency-injection container.
/// </summary>
public static class TelegramServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 Telegram 通知器(<see cref="ITelegramNotifier"/> 與 <see cref="TelegramNotifier"/>,單例)。
    /// Registers the Telegram notifier (<see cref="ITelegramNotifier"/> and <see cref="TelegramNotifier"/>, as a
    /// singleton).
    /// </summary>
    /// <param name="services">服務集合。The service collection.</param>
    /// <param name="configure">設定委派。權杖請從環境變數或加密設定檔取得。The configuration delegate; take the token from an environment variable or an encrypted settings file.</param>
    /// <returns>服務集合本身。The service collection.</returns>
    /// <remarks>
    /// <para>
    /// 設定在註冊當下就驗證,錯了立刻擲出例外,而不是等到第一則告警送不出去才發現 ——
    /// 告警管道壞掉的時候,正是最不容易察覺的時候。
    /// The options are validated at registration and a mistake throws immediately, rather than surfacing when the
    /// first alert fails to send; a broken alert channel is precisely the failure least likely to be noticed.
    /// </para>
    /// <para>
    /// 具名用戶端以 <c>Ozakboy.Http</c> 的 <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/> 組成,
    /// 它替這個用戶端做三件事:移除 <see cref="IHttpClientFactory"/> 的預設日誌(那些日誌會寫出含權杖的完整位址);
    /// 把 <see cref="HttpClient.Timeout"/> 設為無限,逾時交給 <see cref="TelegramNotifierOptions.RequestTimeout"/>
    /// (否則較短的那一個先觸發,而 <see cref="HttpClient"/> 自己的逾時看起來像「被取消」,會被歸成不可重試);
    /// 以及把權杖(完整權杖與冒號後的祕密段)登記到這個用戶端的遮罩器,管線的日誌與門面回傳的錯誤都用它遮罩。
    /// The named client is built with <c>Ozakboy.Http</c>'s
    /// <see cref="OzakboyHttpClientBuilderExtensions.AddOzakboyHttpPipeline"/>, which does three things for it: it
    /// removes <see cref="IHttpClientFactory"/>'s default logging (which writes the full URI, token included); it
    /// sets <see cref="HttpClient.Timeout"/> to infinite and leaves timeouts to
    /// <see cref="TelegramNotifierOptions.RequestTimeout"/> (otherwise whichever is shorter fires first, and
    /// <see cref="HttpClient"/>'s own timeout looks like a cancellation and would be classed as not retryable); and
    /// it registers the token — the full token and the secret half after the colon — on this client's masker, which
    /// the pipeline's logging and the facade's returned errors are both masked with.
    /// </para>
    /// <para>
    /// 管線上實際作用的只有日誌與逾時:簽章用不到而關閉;限流關閉,節流由通知器自己的優先閘門負責 ——
    /// 管線的限流器依抵達順序放行,給不了 Critical 優先,也沒有全通知器共用的 429 暫停,兩邊都開只會重複節流;
    /// 重試處理器設為 <see cref="RetryPolicy.NoRetry"/>,重送收在通知器的迴圈裡(理由見 <see cref="TelegramNotifier"/>)。
    /// 單次與整體逾時都等於 <see cref="TelegramNotifierOptions.RequestTimeout"/>:每一次 HTTP 呼叫就是一次嘗試。
    /// 請求與回應本體的日誌保持關閉,本體裡是 chat id 與訊息內容。門面每次送出都以
    /// <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/> 建立,帶入同一個遮罩器與逾時設定。
    /// Only logging and timeouts are actually at work in the pipeline. Signing is unused and off. Rate limiting is
    /// off because throttling belongs to the notifier's own priority gate: the pipeline's limiter admits in arrival
    /// order, cannot give a Critical priority, and has no 429 pause shared across the notifier, so running both would
    /// only throttle twice. The retry handler is set to <see cref="RetryPolicy.NoRetry"/>, and resending lives in the
    /// notifier's loop (see <see cref="TelegramNotifier"/> for why). The attempt and overall timeouts both equal
    /// <see cref="TelegramNotifierOptions.RequestTimeout"/>: each HTTP call is one attempt. Request and response body
    /// logging stays off, since the body holds the chat id and the message text. The facade is built for each send
    /// with <see cref="OzakboyHttpServiceProviderExtensions.CreateOzakboyHttpPipelineClient"/>, which brings in the
    /// same masker and timeouts.
    /// </para>
    /// <para>
    /// 重複呼叫時只有第一次生效。
    /// Only the first call takes effect when this is called more than once.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// 任一參數為 <see langword="null"/> 時擲出。Thrown when either argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// 設定不合法時擲出。訊息不會回述權杖。Thrown when the options are invalid; the message never echoes the token.
    /// </exception>
    public static IServiceCollection AddTelegramNotifier(this IServiceCollection services, Action<TelegramNotifierOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TelegramNotifierOptions();
        configure(options);

        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(configure));
        }

        // 已經註冊過就不再往同一個具名用戶端疊一條管線:日誌與重試處理器會各掛兩份,而帶鍵的遮罩器以最後一次註冊為準 ——
        // 第二次若換了權杖,實際生效的通知器(第一次註冊的)用的權杖反而不在遮罩清單裡。
        // Once registered, no second pipeline is stacked on the same named client: its logging and retry handlers
        // would be attached twice, and the keyed masker follows the last registration — so a second call with a
        // different token would leave the notifier actually in effect (the first one) with a token the masker does
        // not know.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TelegramNotifier)))
        {
            return services;
        }

        services.AddHttpClient(TelegramNotifier.HttpClientName)
            .AddOzakboyHttpPipeline(pipeline => ConfigurePipeline(pipeline, options));

        services.TryAddSingleton(provider => new TelegramNotifier(
            () => provider.CreateOzakboyHttpPipelineClient(TelegramNotifier.HttpClientName),
            options,
            provider.GetService<ILogger<TelegramNotifier>>(),
            provider.GetService<TimeProvider>()));
        services.TryAddSingleton<ITelegramNotifier>(provider => provider.GetRequiredService<TelegramNotifier>());

        return services;
    }

    /// <summary>
    /// 把 Telegram 用戶端的管線設定成只做日誌、遮罩與逾時。理由見 <see cref="AddTelegramNotifier"/>。
    /// Configures the Telegram client's pipeline to do logging, masking and timeouts only; see
    /// <see cref="AddTelegramNotifier"/> for why.
    /// </summary>
    private static void ConfigurePipeline(HttpPipelineOptions pipeline, TelegramNotifierOptions options)
    {
        // Validate 已確認權杖存在;這裡的擲出只是讓編譯器看得懂,實際上不會發生。
        // Validate has already confirmed the token is present; this throw only informs the compiler.
        var token = options.BotToken ?? throw new ArgumentException("未設定 BotToken。No BotToken is configured.", nameof(options));

        pipeline.EnableSigning = false;
        pipeline.EnableRateLimiting = false;
        pipeline.Retry.Policy = RetryPolicy.NoRetry;

        var timeouts = TelegramNotifier.CreateTimeouts(options);
        pipeline.Timeouts.AttemptTimeout = timeouts.AttemptTimeout;
        pipeline.Timeouts.OverallTimeout = timeouts.OverallTimeout;

        // 本體裡是 chat id 與訊息內容,不進日誌。預設本來就關閉,這裡明寫,讓這條保證不依賴上游預設值。
        // The body holds the chat id and the message text, which stay out of the logs. They are off by default;
        // saying so here keeps the guarantee from resting on an upstream default.
        pipeline.Logging.LogRequestBody = false;
        pipeline.Logging.LogResponseBody = false;

        foreach (var secret in TokenRedactor.SecretLiterals(token))
        {
            pipeline.KnownSecrets.Add(secret);
        }
    }
}
