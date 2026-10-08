using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace TemplateName.Web.Common.Observability;

/// <summary>
/// Keeps an <see cref="ActivitySource"/> listener on ASP.NET Core's request source for the lifetime of the host. Without any
/// listener, hosting may skip creating the request <see cref="Activity"/>, and the trace id would fall back to
/// <c>HttpContext.TraceIdentifier</c>, which is not a W3C trace id. <see cref="ActivitySamplingResult.PropagationData"/> is the
/// cheapest result that still yields a real activity; OpenTelemetry's own listener upgrades sampling when it is configured.
/// </summary>
internal sealed class RequestActivityBackgroundService : BackgroundService
{
    internal const string ActivitySourceName = "Microsoft.AspNetCore";

    private ActivityListener? _listener;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(_listener);

        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _listener?.Dispose();
        base.Dispose();
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
