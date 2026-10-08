using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TemplateName.UnitTests.Web;

/// <summary>Captures <c>OnStarting</c> callbacks so tests can fire them as the server would when the response starts.</summary>
internal sealed class RecordingHttpResponseFeature : HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

    private bool _hasStarted;

    public override bool HasStarted => _hasStarted;

    public int CallbackCount => _callbacks.Count;

    public static RecordingHttpResponseFeature Attach(HttpContext context)
    {
        var feature = new RecordingHttpResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);
        return feature;
    }

    public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));

    public void MarkStarted() => _hasStarted = true;

    public async Task FireOnStartingAsync()
    {
        foreach (var (callback, state) in _callbacks)
        {
            await callback(state);
        }
    }
}
