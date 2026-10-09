using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The Auth outbox publishes its integration events through the real host, outbox and Data Protection key ring.
/// <see cref="RecordingIntegrationEventHandler{TEvent}"/> stands in for the consuming modules (<c>Factory.IntegrationEvents</c>).
/// </summary>
public sealed class IntegrationEventPublishingTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string RegisterRoute = "/api/v1/auth/register";
    private const string ForgotRoute = "/api/v1/auth/password/forgot";
    private const string LoginRoute = "/api/v1/auth/login";
    private const string RefreshRoute = "/api/v1/auth/token/refresh";
    private const string ChangeRoute = "/api/v1/auth/password/change";
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string Email = "alice@example.com";
    private const string ConfirmEmailUrl = "http://localhost:3000/confirm-email?token=";
    private const string ResetPasswordUrl = "http://localhost:3000/reset-password?token=";

    [Fact]
    public async Task Register_then_dispatch_publishes_one_email_verification_event()
    {
        await RegisterAsync(Email);

        Factory.IntegrationEvents.Events.ShouldBeEmpty();
        await DispatchOutboxAsync();

        var published = Factory.IntegrationEvents.Events.ShouldHaveSingleItem().ShouldBeOfType<EmailVerificationRequestedIntegrationEvent>();
        var user = await QueryAsync(context => context.Set<User>().SingleAsync(Ct));
        var code = await QueryAsync(context => context.Set<VerificationCode>().SingleAsync(Ct));
        var message = await OutboxMessageAsync<VerificationCodeIssuedDomainEvent>();
        published.Id.ShouldBe(message.Id);
        published.OccurredAt.ShouldBe(OccurredAt(message));
        published.UserId.ShouldBe(user.Id);
        published.Email.ShouldBe(Email);
        published.ExpiresAt.ShouldBe(code.ExpiresAt);

        // The link is the one emailed, re-protected; the ciphertext holds neither the token nor the address of the link.
        var token = Factory.EmailSender.LastLinkToken(Email);
        Unprotect(published.ProtectedActionUrl).ShouldBe(ConfirmEmailUrl + Uri.EscapeDataString(token));
        published.ProtectedActionUrl.ShouldNotContain(token);
        published.ProtectedActionUrl.ShouldNotContain(Uri.EscapeDataString(token));
        published.ProtectedActionUrl.ShouldNotContain("localhost");
    }

    [Fact]
    public async Task Retried_dispatch_republishes_the_same_id()
    {
        // The second consumer (FlakyIntegrationEventHandler) fails the first time, so the publish is retried; the consumer that
        // succeeded sees the same id twice.
        await RegisterAsync(Email);
        Factory.FlakySwitch.FailNext = true;

        await DispatchOutboxAsync();
        (await OutboxMessageAsync<VerificationCodeIssuedDomainEvent>()).AttemptCount.ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        await DispatchOutboxAsync();

        var message = await OutboxMessageAsync<VerificationCodeIssuedDomainEvent>();
        message.ProcessedAt.ShouldNotBeNull();
        var published = Factory.IntegrationEvents.Events.Cast<EmailVerificationRequestedIntegrationEvent>().ToList();
        published.Count.ShouldBe(2);
        published.ShouldAllBe(integrationEvent => integrationEvent.Id == message.Id && integrationEvent.OccurredAt == OccurredAt(message));

        // Only the publisher failed and ran again: the confirmation email went out once.
        Factory.EmailSender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Forgot_twice_publishes_only_the_live_link()
    {
        await CreateUserAsync(Email);
        await ForgotAsync(Email);
        Factory.Time.Advance(TimeSpan.FromSeconds(60));
        await ForgotAsync(Email);

        // Both codes were issued before the dispatch; the second invalidated the first.
        await DispatchOutboxAsync();

        var codes = await QueryAsync(context => context.Set<VerificationCode>().OrderBy(code => code.CreatedAt).ToListAsync(Ct));
        codes.Count.ShouldBe(2);
        codes[0].InvalidatedAt.ShouldNotBeNull();
        var published = Factory.IntegrationEvents.Events.ShouldHaveSingleItem().ShouldBeOfType<PasswordResetRequestedIntegrationEvent>();
        published.Reason.ShouldBe(PasswordResetReason.SelfService);
        published.ExpiresAt.ShouldBe(codes[1].ExpiresAt);
        Unprotect(published.ProtectedActionUrl).ShouldBe(ResetPasswordUrl + Uri.EscapeDataString(Factory.EmailSender.LastLinkToken(Email)));

        // The stale code was skipped, not failed: every message is processed.
        (await QueryAsync(context => context.Set<OutboxMessage>().CountAsync(message => message.ProcessedAt == null, Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task Admin_create_publishes_reset_with_CreatedByAdmin()
    {
        await SignInAsync(AuthPermissions.UserCreate);
        Guid userId;
        using (var created = await Client.PostAsJsonAsync(UsersRoute, new { email = "New.Person@Example.com", displayName = "New Person", locale = "ms" }, Ct))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            userId = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        }

        await DispatchOutboxAsync();

        var published = Factory.IntegrationEvents.Events.OfType<PasswordResetRequestedIntegrationEvent>().ShouldHaveSingleItem();
        published.Reason.ShouldBe(PasswordResetReason.CreatedByAdmin);
        published.UserId.ShouldBe(userId);
        published.Email.ShouldBe("New.Person@Example.com");
        Unprotect(published.ProtectedActionUrl).ShouldStartWith(ResetPasswordUrl);
    }

    [Fact]
    public async Task Admin_force_reset_publishes_reset_with_ForcedByAdmin()
    {
        var target = await CreateUserAsync(Email);
        await SignInAsync(AuthPermissions.UserResetPassword);
        using (var forced = await Client.PostAsync($"{UsersRoute}/{target.Id}/force-password-reset", content: null, Ct))
        {
            forced.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchOutboxAsync();

        var published = Factory.IntegrationEvents.Events.OfType<PasswordResetRequestedIntegrationEvent>().ShouldHaveSingleItem();
        published.Reason.ShouldBe(PasswordResetReason.ForcedByAdmin);
        published.UserId.ShouldBe(target.Id);
        published.Email.ShouldBe(Email);
    }

    [Fact]
    public async Task Lockout_password_change_and_token_reuse_publish_their_events()
    {
        var user = await CreateUserAsync(Email);

        // Token reuse: the first refresh token, presented again after it was rotated, revokes its session.
        var first = await LoginAsync(Email, AuthTestHarness.Password);
        using (var rotated = await RefreshAsync(first.RefreshToken))
        {
            rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var reused = await RefreshAsync(first.RefreshToken))
        {
            reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // A password change from a second session.
        const string NewPassword = "a brand new passphrase";
        var second = await LoginAsync(Email, AuthTestHarness.Password);
        using (var request = new HttpRequestMessage(HttpMethod.Post, ChangeRoute)
        {
            Content = JsonContent.Create(new { currentPassword = AuthTestHarness.Password, newPassword = NewPassword }),
        }.WithBearer(second.AccessToken))
        using (var changed = await Client.SendAsync(request, Ct))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Five failed sign-ins lock the account.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await Client.PostAsJsonAsync(LoginRoute, new { email = Email, password = "not the password at all" }, Ct);
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await DispatchOutboxAsync();

        var stored = await QueryAsync(context => context.Set<User>().SingleAsync(candidate => candidate.Id == user.Id, Ct));
        var reuse = await OutboxMessageAsync<RefreshTokenReuseDetectedDomainEvent>();
        var change = await OutboxMessageAsync<PasswordChangedDomainEvent>();
        var lockout = await OutboxMessageAsync<UserLockedOutDomainEvent>();
        var events = Factory.IntegrationEvents.Events;
        events.Count.ShouldBe(3);
        events.OfType<RefreshTokenReuseDetectedIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new RefreshTokenReuseDetectedIntegrationEvent(reuse.Id, OccurredAt(reuse), user.Id, first.SessionId));
        events.OfType<PasswordChangedIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new PasswordChangedIntegrationEvent(change.Id, OccurredAt(change), user.Id));
        events.OfType<UserLockedOutIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new UserLockedOutIntegrationEvent(lockout.Id, OccurredAt(lockout), user.Id, stored.LockoutEnd.ShouldNotBeNull()));
    }

    private static DateTimeOffset OccurredAt(OutboxMessage message)
        => new(DateTime.SpecifyKind(message.OccurredAt, DateTimeKind.Utc));

    private string Unprotect(string protectedValue)
        => Factory.Services.GetRequiredService<ISecretProtector>().Unprotect(protectedValue);

    private async Task RegisterAsync(string email)
    {
        using var response = await Client.PostAsJsonAsync(
            RegisterRoute,
            new { email, password = AuthTestHarness.Password, displayName = "Alice", locale = "en" },
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private async Task ForgotAsync(string email)
    {
        using var response = await Client.PostAsJsonAsync(ForgotRoute, new { email }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private async Task<Tokens> LoginAsync(string email, string password)
    {
        using var response = await Client.PostAsJsonAsync(LoginRoute, new { email, password }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) => Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);

    private async Task DispatchOutboxAsync()
    {
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher<AuthDbContext>>();
        int claimed;
        do
        {
            claimed = await dispatcher.ProcessBatchAsync(Ct);
        }
        while (claimed > 0);
    }

    private Task<OutboxMessage> OutboxMessageAsync<TDomainEvent>()
        => QueryAsync(context => context.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Type == typeof(TDomainEvent).FullName, Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
