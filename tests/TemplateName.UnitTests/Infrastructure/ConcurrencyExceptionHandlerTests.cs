using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class ConcurrencyExceptionHandlerTests
{
    [Fact]
    public async Task Concurrency_exception_maps_to_409()
    {
        var (handler, context) = CreateHandler();

        var handled = await handler.TryHandleAsync(context, new DbUpdateConcurrencyException("stale"), TestContext.Current.CancellationToken);

        handled.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(409);
        body.RootElement.GetProperty("code").GetString().ShouldBe("concurrency.conflict");
        body.RootElement.GetRawText().ShouldNotContain("stale");
    }

    [Fact]
    public async Task Other_exceptions_are_not_handled()
    {
        var (handler, context) = CreateHandler();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom"), TestContext.Current.CancellationToken);

        handled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Length.ShouldBe(0);
    }

    private static (ConcurrencyExceptionHandler Handler, HttpContext Context) CreateHandler()
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var handler = new ConcurrencyExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<ConcurrencyExceptionHandler>.Instance);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        return (handler, context);
    }
}
