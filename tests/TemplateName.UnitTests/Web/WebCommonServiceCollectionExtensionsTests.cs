using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Web.Common;
using TemplateName.Web.Common.Identity;

namespace TemplateName.UnitTests.Web;

public sealed class WebCommonServiceCollectionExtensionsTests
{
    [Fact]
    public async Task Problem_details_carry_trace_id_and_default_code()
    {
        using var activity = new Activity("test").Start();
        await using var provider = CreateProvider();
        var (status, body) = await WriteProblemAsync(provider, StatusCodes.Status404NotFound);

        status.ShouldBe(404);
        body.GetProperty("traceId").GetString().ShouldBe(activity.TraceId.ToHexString());
        body.GetProperty("code").GetString().ShouldBe("http.404");
    }

    [Fact]
    public void Registers_current_user_and_throws_on_bad_request()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICurrentUser>().ShouldBeOfType<HttpContextCurrentUser>();
        provider.GetRequiredService<IOptions<RouteHandlerOptions>>().Value.ThrowOnBadRequest.ShouldBeTrue();
    }

    private static ServiceProvider CreateProvider()
    {
        return new ServiceCollection().AddLogging().AddWebCommon().BuildServiceProvider();
    }

    private static async Task<(int Status, JsonElement Body)> WriteProblemAsync(IServiceProvider provider, int status)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = status;

        await provider.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = context });

        context.Response.Body.Position = 0;
        var body = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, body.RootElement.Clone());
    }
}
