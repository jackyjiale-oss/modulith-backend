using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Signs a test in without the sign-in endpoints: it writes a confirmed user, a role holding the named permissions and a session straight
/// to the Auth tables, then mints a real access token with the host's own <see cref="IAccessTokenIssuer"/> on the host's clock.
/// </summary>
internal static class AuthTestHarness
{
    /// <summary>The saved locale of a harness user unless a test asks for another one.</summary>
    internal const string DefaultLocale = "en";

    /// <summary>The password of every harness user (long enough for <c>Auth:Password:MinLength</c>).</summary>
    internal const string Password = "correct-horse-battery-staple";

    private const string AuthMethods = "pwd";
    private const string DeviceName = "Integration tests";
    private const string HarnessDescription = "Created by the integration test harness.";

    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// Creates a confirmed user with <see cref="Password"/> (hashed by the real hasher) and <paramref name="locale"/>, a role of its own
    /// holding <paramref name="permissions"/>, and a session with one refresh token, and returns them with a valid access token. A code no
    /// permission source declares is inserted into <c>auth.Permissions</c> first, so tests can use their own codes.
    /// </summary>
    internal static async Task<SignedInUser> SignInAsync(
        IServiceProvider services,
        IReadOnlyCollection<string> permissions,
        string locale,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var context = provider.GetRequiredService<AuthDbContext>();
        var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();
        var suffix = Guid.NewGuid().ToString("N");

        var role = Role.Create($"Test role {suffix}", HarnessDescription, now).Value;
        var granted = role.SetPermissions(await GetOrCreatePermissionIdsAsync(context, permissions, now, cancellationToken));
        if (granted.IsFailure)
        {
            throw new InvalidOperationException($"The harness role could not be granted its permissions: {granted.Error.Code}.");
        }

        var email = $"user-{suffix}@example.com";
        var user = User.Register(email, "Test user", locale, provider.GetRequiredService<IPasswordHasher>().Hash(Password), now).Value;
        user.ConfirmEmail(now);
        user.AssignRole(role.Id, assignedBy: null, now);

        var refreshToken = provider.GetRequiredService<ISecureTokenService>().Generate();
        var (session, _) = UserSession.Start(
            user.Id,
            AuthMethods,
            DeviceName,
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            refreshToken.Hash,
            SlidingLifetime,
            AbsoluteLifetime,
            now);

        context.AddRange(role, user, session);
        await context.SaveChangesAsync(cancellationToken);

        var accessToken = Issue(services, user, session, now);
        return new SignedInUser(user.Id, email, Password, accessToken, session.Id);
    }

    /// <summary>
    /// Creates a user who can sign in through <c>POST /api/v1/auth/login</c>: <paramref name="password"/> hashed by the host's real hasher
    /// (null for an account an administrator created, which has no password yet), the seeded <c>User</c> role, a confirmed email unless
    /// <paramref name="confirmed"/> is false, and suspended when <paramref name="suspended"/> is true. No session is created.
    /// </summary>
    internal static async Task<User> CreateUserAsync(
        IServiceProvider services,
        string email,
        string? password,
        bool confirmed,
        bool suspended,
        string locale,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var context = provider.GetRequiredService<AuthDbContext>();
        var now = provider.GetRequiredService<TimeProvider>().GetUtcNow();

        var passwordHash = password is null ? null : provider.GetRequiredService<IPasswordHasher>().Hash(password);
        var user = User.Register(email, "Test user", locale, passwordHash, now).Value;
        if (confirmed)
        {
            user.ConfirmEmail(now);
        }

        if (suspended)
        {
            user.Suspend(now);
        }

        var userRole = await context.Set<Role>().SingleAsync(role => role.NormalizedName == Role.NormalizeName(SystemRoles.User), cancellationToken);
        user.AssignRole(userRole.Id, assignedBy: null, now);
        context.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>
    /// A new access token for <paramref name="user"/>'s session, minted by the host behind <paramref name="services"/> on its clock. Use it
    /// after the test moved the clock past the token's lifetime, or for a host derived with <c>WithWebHostBuilder</c>: each host has its
    /// own signing key, and derived hosts share the database, so the user and session are found there.
    /// </summary>
    internal static async Task<string> IssueAccessTokenAsync(IServiceProvider services, SignedInUser user, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var stored = await context.Set<User>().AsNoTracking().SingleAsync(candidate => candidate.Id == user.UserId, cancellationToken);
        var session = await context.Set<UserSession>().AsNoTracking().SingleAsync(candidate => candidate.Id == user.SessionId, cancellationToken);

        return Issue(services, stored, session, session.CreatedAt);
    }

    /// <summary>Sends <paramref name="accessToken"/> as <c>Authorization: Bearer</c> on every request of <paramref name="client"/>; null removes it.</summary>
    internal static void Authorize(HttpClient client, string? accessToken)
        => client.DefaultRequestHeaders.Authorization = accessToken is null ? null : new AuthenticationHeaderValue("Bearer", accessToken);

    private static string Issue(IServiceProvider services, User user, UserSession session, DateTimeOffset authTime)
    {
        var request = new AccessTokenRequest(user.Id, session.Id, session.SecurityStamp, session.AuthMethods, authTime, user.Locale);
        return services.GetRequiredService<IAccessTokenIssuer>().Issue(request).Value;
    }

    private static async Task<Guid[]> GetOrCreatePermissionIdsAsync(
        AuthDbContext context,
        IReadOnlyCollection<string> permissions,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var codes = permissions.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var code in codes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code, nameof(permissions));
        }

        var existing = await context.Set<Permission>().Where(permission => codes.Contains(permission.Code)).ToListAsync(cancellationToken);
        foreach (var code in codes.Except(existing.Select(permission => permission.Code), StringComparer.Ordinal))
        {
            var created = Permission.Create(code, code.Split('.')[0], code, HarnessDescription, now);
            context.Add(created);
            existing.Add(created);
        }

        return [.. existing.Select(permission => permission.Id)];
    }
}
