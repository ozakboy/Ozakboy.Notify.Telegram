using Microsoft.Extensions.DependencyInjection;
using Ozakboy.Notify.Telegram.Tests.TestSupport;

namespace Ozakboy.Notify.Telegram.Tests;

[TestClass]
public sealed class TelegramNotifierOptionsTests
{
    [TestMethod]
    public void Validate_WithTokenAndChatId_Succeeds()
    {
        var result = TestValues.Options().Validate();

        Assert.IsTrue(result.IsSuccess, result.ToString());
    }

    [TestMethod]
    public void Defaults_HaveNoTokenAndFollowTelegramLimits()
    {
        var options = new TelegramNotifierOptions();

        Assert.IsNull(options.BotToken, "不可提供任何預設權杖。No default token may be provided.");
        Assert.IsNull(options.ChatId);
        Assert.AreEqual(new Uri("https://api.telegram.org/"), options.ApiBaseAddress);
        Assert.AreEqual(TimeSpan.FromSeconds(1), options.MinimumInterval);
        Assert.AreEqual(20, options.MaxMessagesPerMinute);
        Assert.AreSame(TelegramNotifierOptions.DefaultTransientRetry, options.TransientRetry);
    }

    [TestMethod]
    public void Validate_WithoutToken_Fails()
    {
        var result = TestValues.Options(options => options.BotToken = null).Validate();

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TelegramErrorCodes.InvalidOptions, result.Error!.Code);
        Assert.AreEqual(ErrorCategory.Validation, result.Error.Category);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("123456")]
    [DataRow("123456:tooShortSecret")]
    [DataRow("12a456:AAFakeTokenForUnitTestsOnly_0123456789ab")]
    [DataRow(":AAFakeTokenForUnitTestsOnly_0123456789ab")]
    [DataRow("123456:AAFakeTokenForUnitTestsOnly_0123456789ab ")]
    [DataRow("\"123456:AAFakeTokenForUnitTestsOnly_0123456789ab\"")]
    [DataRow("123456:AAFakeToken/ForUnitTestsOnly_0123456789ab")]
    public void Validate_WithMalformedToken_FailsWithoutEchoingIt(string token)
    {
        var result = TestValues.Options(options => options.BotToken = token).Validate();

        Assert.IsTrue(result.IsFailure, $"應判為格式不符。Should be rejected: [{token.Length} chars]");
        Assert.AreEqual(TelegramErrorCodes.InvalidOptions, result.Error!.Code);
        Assert.IsFalse(result.Error.Message.Contains(token.Trim(), StringComparison.Ordinal), "錯誤訊息不可回述權杖。The message must not echo the token.");
    }

    [TestMethod]
    [DataRow("-1001234567890")]
    [DataRow("123456789")]
    [DataRow("@my_alert_channel")]
    public void Validate_WithWellFormedChatId_Succeeds(string chatId)
    {
        var result = TestValues.Options(options => options.ChatId = chatId).Validate();

        Assert.IsTrue(result.IsSuccess, result.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("abc")]
    [DataRow("@")]
    [DataRow("@bad-name")]
    [DataRow(" 123")]
    public void Validate_WithMalformedChatId_Fails(string chatId)
    {
        var result = TestValues.Options(options => options.ChatId = chatId).Validate();

        Assert.IsTrue(result.IsFailure);
        Assert.AreEqual(TelegramErrorCodes.InvalidOptions, result.Error!.Code);
    }

    [TestMethod]
    [DataRow("http://api.telegram.org/")]
    [DataRow("https://user:pass@api.telegram.org/")]
    [DataRow("https://api.telegram.org/?x=1")]
    [DataRow("ftp://api.telegram.org/")]
    public void Validate_WithUnsafeBaseAddress_Fails(string address)
    {
        var result = TestValues.Options(options => options.ApiBaseAddress = new Uri(address)).Validate();

        Assert.IsTrue(result.IsFailure, address);
    }

    [TestMethod]
    [DataRow("http://localhost:8081/")]
    [DataRow("http://127.0.0.1:8081/")]
    [DataRow("https://bot-api.example.test/custom")]
    public void Validate_WithHttpsOrLoopbackHttp_Succeeds(string address)
    {
        var result = TestValues.Options(options => options.ApiBaseAddress = new Uri(address)).Validate();

        Assert.IsTrue(result.IsSuccess, result.ToString());
    }

    [TestMethod]
    public void Validate_RejectsOutOfRangeNumbers()
    {
        Action<TelegramNotifierOptions>[] invalid =
        [
            options => options.RequestTimeout = TimeSpan.Zero,
            options => options.MinimumInterval = TimeSpan.Zero,
            options => options.ThrottleWaitLimit = TimeSpan.FromSeconds(-1),
            options => options.MaxMessagesPerMinute = 0,
            options => options.MaxRateLimitRetries = -1,
            options => options.MaxRetryAfter = TimeSpan.Zero,
            options => options.MaxRetryAfter = TelegramNotifierOptions.MaxRetryAfterLimit + TimeSpan.FromSeconds(1),
            options => options.MaxPartsPerMessage = 0,
            options => options.MaxPartsPerMessage = TelegramNotifierOptions.MaxPartsPerMessageLimit + 1,
            options => options.TransientRetry = null!,
        ];

        for (var i = 0; i < invalid.Length; i++)
        {
            var result = TestValues.Options(invalid[i]).Validate();
            Assert.IsTrue(result.IsFailure, $"第 {i} 組設定應判為不合法。Case {i} should be invalid.");
        }
    }

    [TestMethod]
    public void Constructor_WithInvalidOptions_ThrowsWithoutEchoingToken()
    {
        const string ShortToken = "123456:AAshortButSecretish";
        using var client = new HttpClient();

        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => new TelegramNotifier(client, TestValues.Options(options => options.BotToken = ShortToken)));

        Assert.IsFalse(exception.Message.Contains(ShortToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public void AddTelegramNotifier_WithInvalidOptions_ThrowsWithoutEchoingToken()
    {
        const string ShortToken = "123456:AAshortButSecretish";
        var services = new ServiceCollection();

        var exception = Assert.ThrowsExactly<ArgumentException>(
            () => services.AddTelegramNotifier(options =>
            {
                options.BotToken = ShortToken;
                options.ChatId = TestValues.ChatId;
            }));

        Assert.IsFalse(exception.Message.Contains(ShortToken, StringComparison.Ordinal));
    }

    [TestMethod]
    public void AddTelegramNotifier_RegistersOneSingletonAndAnInfiniteTimeoutClient()
    {
        var services = new ServiceCollection();
        services.AddTelegramNotifier(options =>
        {
            options.BotToken = TestValues.BotToken;
            options.ChatId = TestValues.ChatId;
        });

        using var provider = services.BuildServiceProvider();
        var viaInterface = provider.GetRequiredService<ITelegramNotifier>();
        var concrete = provider.GetRequiredService<TelegramNotifier>();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(TelegramNotifier.HttpClientName);

        Assert.AreSame(concrete, viaInterface);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [TestMethod]
    public void AddTelegramNotifier_RejectsNullArguments()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ServiceCollection().AddTelegramNotifier(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => TelegramServiceCollectionExtensions.AddTelegramNotifier(null!, _ => { }));
    }

    [TestMethod]
    public void Notification_RejectsUndefinedLevelAndNullText()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new TelegramNotification((NotificationLevel)42, "x"));
        Assert.ThrowsExactly<ArgumentNullException>(() => new TelegramNotification(NotificationLevel.Info, null!));
    }
}
