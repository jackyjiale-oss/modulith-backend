using System.Diagnostics;
using TemplateName.Web.Common.Observability;

namespace TemplateName.UnitTests.Web;

public sealed class RequestActivityBackgroundServiceTests
{
    [Fact]
    public async Task Request_activities_are_created_with_a_w3c_trace_id_once_started()
    {
        using var sut = new RequestActivityBackgroundService();
        await sut.StartAsync(TestContext.Current.CancellationToken);
        using var source = new ActivitySource(RequestActivityBackgroundService.ActivitySourceName);

        using var activity = source.StartActivity("request", ActivityKind.Server);

        activity.ShouldNotBeNull();
        activity.TraceId.ToHexString().ShouldMatch("^[0-9a-f]{32}$");
        await sut.StopAsync(TestContext.Current.CancellationToken);
    }
}
