using Microsoft.AspNetCore.Mvc.Testing;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for tests that call the running API through <see cref="Client"/>. Each test starts with empty databases seeded with the
/// Auth module's system roles and the permissions every module declares, the clock at its start instant, an anonymous client and user,
/// no extra declared permissions, no recorded domain or integration events or emails and handlers that neither fail nor wait. Only the
/// Data Protection key ring (<c>auth.DataProtectionKeys</c>) is kept from earlier tests, because the hosts keep it in memory.
/// </summary>
public abstract class IntegrationTestBase(IntegrationTestWebAppFactory factory) : IAsyncLifetime
{
    private SignedInUser? _signedInUser;

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
        Factory.IntegrationEvents.Clear();
        Factory.MessageContextRecorder.Clear();
        Factory.EmailSender.Clear();
        Factory.FlakySwitch.Reset();
        Factory.HandlerGate.Reset();
    }

    public virtual ValueTask DisposeAsync()
    {
        Client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a confirmed user (saved locale <c>en</c>) whose own role holds <paramref name="permissions"/> (permission codes; a code no
    /// module declares is created) and a session, and makes <see cref="Client"/> send that user's access token. Signing in again replaces
    /// the user.
    /// </summary>
    protected Task<SignedInUser> SignInAsync(params string[] permissions) => SignInAsync(permissions, AuthTestHarness.DefaultLocale);

    /// <summary>
    /// <see cref="SignInAsync(string[])"/> with the user's saved <paramref name="locale"/>, the token's <c>locale</c> claim. The saved
    /// locale wins over <c>Accept-Language</c> (decision D7); one the API does not support leaves the choice to the header.
    /// </summary>
    protected async Task<SignedInUser> SignInAsync(IReadOnlyCollection<string> permissions, string locale)
    {
        var user = await AuthTestHarness.SignInAsync(Factory.Services, permissions, locale, Ct);
        AuthTestHarness.Authorize(Client, user.AccessToken);
        _signedInUser = user;
        return user;
    }

    /// <summary>
    /// Creates a user who signs in through the login endpoint with <paramref name="password"/> (<see cref="AuthTestHarness.Password"/>
    /// by default; null for an administrator-created account without one). <see cref="Client"/> stays as it is.
    /// </summary>
    internal Task<User> CreateUserAsync(
        string email,
        string? password = AuthTestHarness.Password,
        bool confirmed = true,
        bool suspended = false,
        string locale = AuthTestHarness.DefaultLocale)
        => AuthTestHarness.CreateUserAsync(Factory.Services, email, password, confirmed, suspended, locale, Ct);

    /// <summary>Makes <see cref="Client"/> anonymous again; the user and session stay in the database.</summary>
    protected void SignOut()
    {
        AuthTestHarness.Authorize(Client, accessToken: null);
        _signedInUser = null;
    }

    /// <summary>Gives <see cref="Client"/> a new access token for the signed-in user, issued now; for tests that move the clock past its lifetime.</summary>
    protected async Task<SignedInUser> RenewAccessTokenAsync()
    {
        var user = _signedInUser ?? throw new InvalidOperationException("No user is signed in; call SignInAsync first.");
        var renewed = user with { AccessToken = await AuthTestHarness.IssueAccessTokenAsync(Factory.Services, user, Ct) };
        AuthTestHarness.Authorize(Client, renewed.AccessToken);
        _signedInUser = renewed;
        return renewed;
    }

    /// <summary>
    /// A client for <paramref name="host"/> (derived with <c>WithWebHostBuilder</c>) that sends the signed-in user's token, minted by
    /// that host because each host has its own signing key; anonymous when nobody is signed in. The caller disposes it.
    /// </summary>
    protected async Task<HttpClient> CreateClientAsync(WebApplicationFactory<Program> host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var accessToken = _signedInUser is { } user ? await AuthTestHarness.IssueAccessTokenAsync(host.Services, user, Ct) : null;
        var client = host.CreateClient();
        AuthTestHarness.Authorize(client, accessToken);
        return client;
    }
}
