using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TemplateName.Web.Common.Errors;

namespace TemplateName.UnitTests.Web;

public sealed class GlobalExceptionHandlerTests
{
    private const string ExceptionMessage = "database password is hunter2";

    [Fact]
    public async Task Development_includes_exception_detail()
    {
        var (handled, status, body) = await HandleAsync(Environments.Development, new InvalidOperationException(ExceptionMessage));

        handled.ShouldBeTrue();
        status.ShouldBe(500);
        body.GetProperty("exceptionDetails").GetString()!.ShouldContain(ExceptionMessage);
        body.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
        body.GetProperty("code").GetString().ShouldBe("server.unexpected_error");
    }

    [Fact]
    public async Task Production_hides_exception_detail()
    {
        var (handled, status, body) = await HandleAsync(Environments.Production, new InvalidOperationException(ExceptionMessage));

        handled.ShouldBeTrue();
        status.ShouldBe(500);
        body.GetProperty("detail").GetString().ShouldBe("An unexpected error occurred.");
        body.GetRawText().ShouldNotContain(ExceptionMessage);
        body.GetRawText().ShouldNotContain("   at ");
        body.TryGetProperty("exceptionDetails", out _).ShouldBeFalse();
        body.GetProperty("code").GetString().ShouldBe("server.unexpected_error");
    }

    [Fact]
    public async Task BadHttpRequestException_maps_to_400_request_malformed()
    {
        var (handled, status, body) = await HandleAsync(Environments.Production, new BadHttpRequestException("bad body"));

        handled.ShouldBeTrue();
        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("request.malformed");
        body.GetProperty("detail").GetString().ShouldBe("The request is malformed.");
        body.GetRawText().ShouldNotContain("bad body");
    }

    [Fact]
    public async Task Response_already_started_is_not_handled()
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var handler = new GlobalExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<GlobalExceptionHandler>.Instance,
            Substitute.For<IHostEnvironment>());
        var context = new DefaultHttpContext { RequestServices = services };
        RecordingHttpResponseFeature.Attach(context).MarkStarted();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("late"), TestContext.Current.CancellationToken);

        handled.ShouldBeFalse();
    }

    private static async Task<(bool Handled, int Status, JsonElement Body)> HandleAsync(string environmentName, Exception exception)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        var handler = new GlobalExceptionHandler(
            services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<GlobalExceptionHandler>.Instance,
            environment);
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, exception, TestContext.Current.CancellationToken);

        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return (handled, context.Response.StatusCode, body.RootElement.Clone());
    }
}
