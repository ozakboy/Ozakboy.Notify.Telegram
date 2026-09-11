using System.Net;
using System.Text;

namespace Ozakboy.Notify.Telegram.Tests.TestSupport;

/// <summary>
/// 假的最內層處理器:記錄收到的請求(位址、本體、內容型別、當下的假時鐘時間),並依腳本回應。測試全程不連網。
/// A stub innermost handler that records each request (URI, body, content type, and the fake-clock time) and
/// replies from a script. Tests never touch the network.
/// </summary>
internal sealed class ScriptedTelegramHandler : HttpMessageHandler
{
    public const string OkBody = """{"ok":true,"result":{"message_id":1,"date":0,"chat":{"id":1,"type":"private"},"text":"echo"}}""";

    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _responder;
    private readonly TimeProvider _clock;
    private int _callCount;

    public ScriptedTelegramHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder, TimeProvider clock)
    {
        _responder = responder;
        _clock = clock;
    }

    public List<RecordedRequest> Requests { get; } = [];

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// 每次都回同一個狀態碼與本體。
    /// Always replies with the same status and body.
    /// </summary>
    public static Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Always(HttpStatusCode status, string body) =>
        (_, _, _) => Task.FromResult(Json(status, body));

    /// <summary>
    /// 依序回應;腳本用完後重複最後一個。
    /// Replies in order, repeating the last entry once the script runs out.
    /// </summary>
    public static Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Sequence(params (HttpStatusCode Status, string Body)[] script) =>
        (_, attempt, _) =>
        {
            var (status, body) = script[Math.Min(attempt - 1, script.Length - 1)];
            return Task.FromResult(Json(status, body));
        };

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static string ErrorBody(int errorCode, string description, string? parameters = null)
    {
        var head = "{\"ok\":false,\"error_code\":" + errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"description\":\"" + description + "\"";
        return parameters is null ? head + "}" : head + ",\"parameters\":{" + parameters + "}}";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _callCount);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        lock (Requests)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                body,
                request.Content?.Headers.ContentType?.ToString(),
                _clock.GetUtcNow()));
        }

        return await _responder(request, attempt, cancellationToken);
    }

    internal sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body, string? ContentType, DateTimeOffset At);
}
