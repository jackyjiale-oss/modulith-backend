using TemplateName.Modules.Auth;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for tests that call the running API through <see cref="Client"/>. Each test starts with empty databases seeded with the
/// Auth module's system roles and permissions, the clock at its start instant, an anonymous user, no extra declared permissions, no
/// recorded events and handlers that neither fail nor wait.
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
        Factory.PermissionSource.Reset();

        // The reset removed the seeded roles and permissions; seed them again.
        await Factory.Services.SeedAuthModuleAsync(Ct);
        Factory.CurrentUser.UserId = null;
        Factory.EventRecorder.Clear();
        Factory.FlakySwitch.Reset();
        Factory.HandlerGate.Reset();
    }

    public virtual ValueTask DisposeAsync()
    {
        Client.Dispose();
        return ValueTask.CompletedTask;
    }
}
