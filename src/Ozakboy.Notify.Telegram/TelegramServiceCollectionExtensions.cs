using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
    /// 具名用戶端會移除 <see cref="IHttpClientFactory"/> 的預設日誌(<c>RemoveAllLoggers()</c>),
    /// 因為那些日誌會寫出含權杖的完整位址。<see cref="HttpClient.Timeout"/> 設為無限,逾時由
    /// <see cref="TelegramNotifierOptions.RequestTimeout"/> 負責 —— 否則較短的那一個會先觸發,
    /// 而 <see cref="HttpClient"/> 自己的逾時看起來像「被取消」,會被歸成不可重試。
    /// The named client has <see cref="IHttpClientFactory"/>'s default logging removed
    /// (<c>RemoveAllLoggers()</c>), because that logging writes the full URI, token included.
    /// <see cref="HttpClient.Timeout"/> is set to infinite and timeouts are left to
    /// <see cref="TelegramNotifierOptions.RequestTimeout"/>; otherwise whichever is shorter fires first, and
    /// <see cref="HttpClient"/>'s own timeout looks like a cancellation and would be classed as not retryable.
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

        services.AddHttpClient(TelegramNotifier.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();

        services.TryAddSingleton(provider => new TelegramNotifier(
            provider.GetRequiredService<IHttpClientFactory>(),
            options,
            provider.GetService<ILogger<TelegramNotifier>>(),
            provider.GetService<TimeProvider>()));
        services.TryAddSingleton<ITelegramNotifier>(provider => provider.GetRequiredService<TelegramNotifier>());

        return services;
    }
}
