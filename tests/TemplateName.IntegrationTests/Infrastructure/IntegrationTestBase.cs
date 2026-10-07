namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for tests that call the running API through <see cref="Client"/>. Each test starts with empty databases, the clock at
/// its start instant, an anonymous user, no recorded events and handlers that do not fail.
/// </summary>
public abstract class IntegrationTestBase(IntegrationTestWebAppFactory factory) : IAsyncLifetime
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected IntegrationTestWebAppFactory Factory { get; } = factory;

    protected HttpClient Client { get; } = factory.CreateClient();

    public virtual async ValueTask InitializeAsync()
    {
        await Factory.ResetDatabasesAsync(Ct);
        Factory.Time.AdjustTime(Factory.Time.Start);
        Factory.CurrentUser.UserId = null;
        Factory.EventRecorder.Clear();
        Factory.FlakySwitch.Reset();
    }

    public virtual ValueTask DisposeAsync()
    {
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}
