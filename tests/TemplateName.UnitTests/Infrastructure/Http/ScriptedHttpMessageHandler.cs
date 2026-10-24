using System.Net;

namespace TemplateName.UnitTests.Infrastructure.Http;

/// <summary>An <see cref="HttpMessageHandler"/> that counts its calls and answers each from a script.</summary>
internal sealed class ScriptedHttpMessageHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;

    /// <summary>The number of requests that reached this handler.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Answers every request with <paramref name="statusCode"/>.</summary>
    public static ScriptedHttpMessageHandler Always(HttpStatusCode statusCode) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));

    /// <summary>Never answers; a request ends only when its token is cancelled.</summary>
    public static ScriptedHttpMessageHandler Hanging() => new(HangAsync);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(Interlocked.Increment(ref _calls), cancellationToken);

    private static async Task<HttpResponseMessage> HangAsync(int call, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
