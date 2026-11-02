using System.Collections.Concurrent;
using System.Net;

namespace TemplateName.UnitTests.Infrastructure.Http;

/// <summary>An <see cref="HttpMessageHandler"/> that counts its calls, remembers each request's method and address, and answers each from a script.</summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private int _calls;

    /// <summary>Answers each request from <paramref name="respond"/>, given the number of the call (the first is 1).</summary>
    public ScriptedHttpMessageHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
        : this((call, _, cancellationToken) => respond(call, cancellationToken))
    {
    }

    /// <summary>Answers each request from <paramref name="respond"/>, given the number of the call (the first is 1) and the request.</summary>
    public ScriptedHttpMessageHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    /// <summary>The number of requests that reached this handler.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The method and address of every request that reached this handler, in order.</summary>
    public IReadOnlyCollection<RecordedRequest> Requests => _requests;

    /// <summary>Answers every request with <paramref name="statusCode"/>.</summary>
    public static ScriptedHttpMessageHandler Always(HttpStatusCode statusCode) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)));

    /// <summary>Never answers; a request ends only when its token is cancelled.</summary>
    public static ScriptedHttpMessageHandler Hanging() => new(HangAsync);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri ?? throw new InvalidOperationException("A request without an address.")));
        return _respond(Interlocked.Increment(ref _calls), request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> HangAsync(int call, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>A request that reached the handler.</summary>
    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri);
}
