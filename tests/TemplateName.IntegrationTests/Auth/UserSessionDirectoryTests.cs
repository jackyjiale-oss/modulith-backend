using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <see cref="IUserSessionDirectory"/> as a consuming module calls it: one Dapper query on <c>auth.UserSessions</c> that must
/// repeat the rule of <see cref="UserSession.IsActive"/> (not revoked, before its expiry) and keep to the one user.
/// </summary>
public sealed class UserSessionDirectoryTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(1);

    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    [Fact]
    public async Task Active_sessions_are_returned()
    {
        var user = await CreateUserAsync("two-sessions@example.com");
        var first = await StartSessionAsync(user.Id);
        var second = await StartSessionAsync(user.Id);

        (await ActiveAsync(user.Id, first, second)).ShouldBe([first, second], ignoreOrder: true);
    }

    [Fact]
    public async Task A_logged_out_session_is_not_returned()
    {
        var user = await CreateUserAsync("logged-out@example.com");
        var kept = await StartSessionAsync(user.Id);
        var revoked = await StartSessionAsync(user.Id);
        await RevokeAsync(revoked);

        (await ActiveAsync(user.Id, kept, revoked)).ShouldBe([kept]);
    }

    [Fact]
    public async Task An_expired_session_is_not_returned()
    {
        var user = await CreateUserAsync("expired@example.com");
        var session = await StartSessionAsync(user.Id);

        Factory.Time.Advance(AbsoluteLifetime + TimeSpan.FromMinutes(1));

        (await ActiveAsync(user.Id, session)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Another_users_session_is_not_returned()
    {
        var owner = await CreateUserAsync("owner@example.com");
        var other = await CreateUserAsync("other@example.com");
        var othersSession = await StartSessionAsync(other.Id);

        (await ActiveAsync(owner.Id, othersSession)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_input_answers_empty()
    {
        var user = await CreateUserAsync("nothing@example.com");
        await StartSessionAsync(user.Id);

        (await ActiveAsync(user.Id)).ShouldBeEmpty();
    }

    private async Task<IReadOnlySet<Guid>> ActiveAsync(Guid userId, params Guid[] sessionIds)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserSessionDirectory>().GetActiveSessionIdsAsync(userId, sessionIds, Ct);
    }

    private async Task<Guid> StartSessionAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        var (session, _) = UserSession.Start(
            userId,
            AuthenticationMethods.Password,
            "Test device",
            userAgent: null,
            ipAddress: null,
            user.SecurityStamp,
            new byte[32],
            SlidingLifetime,
            AbsoluteLifetime,
            Factory.Time.GetUtcNow());
        context.Add(session);
        await context.SaveChangesAsync(Ct);
        return session.Id;
    }

    private async Task RevokeAsync(Guid sessionId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var session = await context.Set<UserSession>().SingleAsync(candidate => candidate.Id == sessionId, Ct);
        session.Revoke(SessionRevokedReason.Logout, Factory.Time.GetUtcNow());
        await context.SaveChangesAsync(Ct);
    }
}
