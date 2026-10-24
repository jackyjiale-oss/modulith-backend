using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Security;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
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
    private const string SetupRoute = "/api/v1/auth/mfa/totp/setup";
    private const string ConfirmRoute = "/api/v1/auth/mfa/totp/confirm";
    private const string RegenerateRoute = "/api/v1/auth/mfa/recovery-codes/regenerate";
    private const string MethodsRoute = "/api/v1/auth/mfa/methods";
    private const string StepUpRoute = "/api/v1/auth/step-up";
    private const string VerifyRoute = "/api/v1/auth/mfa/verify";
    private const string UsersRoute = "/api/v1/admin/auth/users";
    private const string Email = "alice@example.com";
    private const string ConfirmEmailUrl = "http://localhost:3000/confirm-email?token=";
    private const string ResetPasswordUrl = "http://localhost:3000/reset-password?token=";

    private int _addressCount;

    [Fact]
    public async Task Register_then_dispatch_publishes_one_email_verification_event()
    {
        await RegisterAsync(Email);

        Factory.IntegrationEvents.Events.ShouldBeEmpty();
        await DispatchNotificationsAsync();

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

        await DispatchNotificationsAsync();
        (await OutboxMessageAsync<VerificationCodeIssuedDomainEvent>()).AttemptCount.ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        await DispatchNotificationsAsync();

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
        await DispatchNotificationsAsync();

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

        await DispatchNotificationsAsync();

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

        await DispatchNotificationsAsync();

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

        await DispatchNotificationsAsync();

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

    [Fact]
    public async Task Enabling_disabling_and_regenerating_publish_mfa_changed()
    {
        var user = await CreateUserAsync(Email);
        var login = await SignInOnDeviceAsync("Laptop");
        AuthTestHarness.Authorize(Client, login.AccessToken);
        var (methodId, secret) = await SetUpAuthenticatorAsync();

        Factory.Time.Advance(TimeSpan.FromSeconds(20));
        using (var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = CodeAt(secret) }, Ct))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // A later time step proves the step-up the next two actions need.
        Factory.Time.Advance(TimeSpan.FromSeconds(60));
        await StepUpAsync(new { code = CodeAt(secret) });
        using (var regenerate = await Client.PostAsync(RegenerateRoute, content: null, Ct))
        {
            regenerate.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        using (var disable = await Client.DeleteAsync($"{MethodsRoute}/{methodId}", Ct))
        {
            disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await DispatchNotificationsAsync();

        var messages = (await QueryAsync(context => context.Set<OutboxMessage>().AsNoTracking()
            .Where(message => message.Type == typeof(MfaChangedDomainEvent).FullName)
            .ToListAsync(Ct))).OrderBy(message => message.OccurredAt).ToList();
        var published = Factory.IntegrationEvents.Events.OfType<MfaChangedIntegrationEvent>().OrderBy(integrationEvent => integrationEvent.OccurredAt).ToList();
        published.Select(integrationEvent => integrationEvent.Change)
            .ShouldBe([MfaChangeKind.Enabled, MfaChangeKind.RecoveryCodesRegenerated, MfaChangeKind.Disabled]);
        published.ShouldAllBe(integrationEvent => integrationEvent.UserId == user.Id);

        // Each carries the id and time of its outbox message.
        messages.Count.ShouldBe(3);
        published.Select(integrationEvent => integrationEvent.Id).ShouldBe(messages.Select(message => message.Id));
        published.Select(integrationEvent => integrationEvent.OccurredAt).ShouldBe(messages.Select(OccurredAt));

        // The first session of the account is not a new device, and no other sign-in happened.
        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Recovery_code_sign_in_publishes_RecoveryCodeUsed()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);

        var challengeToken = await ChallengeAsync("Laptop");
        using (var verify = await PostAnonymousAsync(VerifyRoute, new { challengeToken, recoveryCode = enrollment.RecoveryCodes[0] }))
        {
            verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await DispatchNotificationsAsync();

        var message = await OutboxMessageAsync<MfaChangedDomainEvent>();
        Factory.IntegrationEvents.Events.OfType<MfaChangedIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new MfaChangedIntegrationEvent(message.Id, OccurredAt(message), user.Id, MfaChangeKind.RecoveryCodeUsed));
    }

    [Fact]
    public async Task Totp_sign_in_publishes_no_mfa_changed()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);

        var challengeToken = await ChallengeAsync("Laptop");
        using (var verify = await PostAnonymousAsync(VerifyRoute, new { challengeToken, code = enrollment.CodeAt(Factory.Time.GetUtcNow()) }))
        {
            verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await DispatchNotificationsAsync();

        Factory.IntegrationEvents.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Admin_reset_publishes_ResetByAdmin()
    {
        var target = await CreateUserAsync(Email);
        await EnrollTotpAsync(target.Id);
        await SignInAsync(AuthPermissions.UserResetMfa);

        using (var reset = await Client.PostAsync($"{UsersRoute}/{target.Id}/reset-mfa", content: null, Ct))
        {
            reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await DispatchNotificationsAsync();

        var message = await OutboxMessageAsync<MfaChangedDomainEvent>();
        Factory.IntegrationEvents.Events.OfType<MfaChangedIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new MfaChangedIntegrationEvent(message.Id, OccurredAt(message), target.Id, MfaChangeKind.ResetByAdmin));
    }

    [Fact]
    public async Task First_sign_in_is_not_a_new_device()
    {
        await CreateUserAsync(Email);

        await SignInOnDeviceAsync("Laptop");
        await DispatchNotificationsAsync();

        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Second_device_publishes_new_device_and_the_same_device_again_does_not()
    {
        var user = await CreateUserAsync(Email);
        await SignInOnDeviceAsync("Laptop");

        var phone = await SignInOnDeviceAsync("Phone", address: "198.51.100.77");
        await SignInOnDeviceAsync("Phone");
        await SignInOnDeviceAsync("Laptop");
        await DispatchNotificationsAsync();

        var message = await OutboxMessageAsync<NewDeviceSignedInDomainEvent>();
        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new NewDeviceSignedInIntegrationEvent(message.Id, OccurredAt(message), user.Id, phone.SessionId, "198.51.100.77"));
    }

    [Fact]
    public async Task A_dormant_account_whose_sessions_were_cleaned_up_still_publishes_new_device()
    {
        var user = await CreateUserAsync(Email);
        await SignInOnDeviceAsync("Laptop");

        // As AuthSessionsCleanup leaves a dormant account: it signed in before, but none of its sessions is stored any more.
        await QueryAsync(context => context.Set<UserSession>().Where(session => session.UserId == user.Id).ExecuteDeleteAsync(Ct));

        var phone = await SignInOnDeviceAsync("Phone", address: "198.51.100.77");
        await DispatchNotificationsAsync();

        var message = await OutboxMessageAsync<NewDeviceSignedInDomainEvent>();
        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().ShouldHaveSingleItem()
            .ShouldBe(new NewDeviceSignedInIntegrationEvent(message.Id, OccurredAt(message), user.Id, phone.SessionId, "198.51.100.77"));
    }

    [Fact]
    public async Task A_device_is_told_apart_by_name_and_user_agent_exactly()
    {
        await CreateUserAsync(Email);
        await SignInOnDeviceAsync("Phone", userAgent: "Agent/1");

        // Another agent, another case, and no agent at all are each a device the user has not signed in from.
        await SignInOnDeviceAsync("Phone", userAgent: "Agent/2");
        await SignInOnDeviceAsync("phone", userAgent: "Agent/1");
        await SignInOnDeviceAsync("Phone");

        // The same pair again, and the same pair without an agent again, are known.
        await SignInOnDeviceAsync("Phone", userAgent: "Agent/1");
        await SignInOnDeviceAsync("Phone");
        await DispatchNotificationsAsync();

        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().Count().ShouldBe(3);
    }

    [Fact]
    public async Task A_new_device_after_the_challenge_publishes_new_device_too()
    {
        var user = await CreateUserAsync(Email);
        var enrollment = await EnrollTotpAsync(user.Id);
        var first = await ChallengeAsync("Laptop");
        using (var verify = await PostAnonymousAsync(VerifyRoute, new { challengeToken = first, code = enrollment.CodeAt(Factory.Time.GetUtcNow()) }))
        {
            verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Factory.Time.Advance(TimeSpan.FromSeconds(60));
        var second = await ChallengeAsync("Phone");
        using var secondVerify = await PostAnonymousAsync(VerifyRoute, new { challengeToken = second, code = enrollment.CodeAt(Factory.Time.GetUtcNow()) });
        secondVerify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionId = (await secondVerify.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("sessionId").GetGuid();

        await DispatchNotificationsAsync();

        Factory.IntegrationEvents.Events.OfType<NewDeviceSignedInIntegrationEvent>().ShouldHaveSingleItem().SessionId.ShouldBe(sessionId);
    }

    [Fact]
    public async Task Events_carry_no_secret()
    {
        const string FirstDevice = "Home PC of the Wilsons";
        const string SecondDevice = "Secret Phone of Alice";
        await CreateUserAsync(Email);
        var home = await SignInOnDeviceAsync(FirstDevice);
        AuthTestHarness.Authorize(Client, home.AccessToken);
        var (_, secret) = await SetUpAuthenticatorAsync();
        Factory.Time.Advance(TimeSpan.FromSeconds(20));
        using var confirm = await Client.PostAsJsonAsync(ConfirmRoute, new { code = CodeAt(secret) }, Ct);
        confirm.StatusCode.ShouldBe(HttpStatusCode.OK);
        var recoveryCodes = (await confirm.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("recoveryCodes")
            .EnumerateArray().Select(item => item.GetString()!).ToList();
        SignOut();

        // A second device signs in with a recovery code: a new device and a recovery-code event, each with secrets around it.
        var challengeToken = await ChallengeAsync(SecondDevice);
        using var verify = await PostAnonymousAsync(VerifyRoute, new { challengeToken, recoveryCode = recoveryCodes[0] });
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tokens = await verify.Content.ReadFromJsonAsync<JsonElement>(Ct);
        await DispatchNotificationsAsync();

        var published = Factory.IntegrationEvents.Events;
        published.OfType<MfaChangedIntegrationEvent>().Select(integrationEvent => integrationEvent.Change)
            .ShouldBe([MfaChangeKind.Enabled, MfaChangeKind.RecoveryCodeUsed], ignoreOrder: true);
        published.OfType<NewDeviceSignedInIntegrationEvent>().ShouldHaveSingleItem();

        var secrets = new List<string>
        {
            Convert.ToBase64String(secret),
            Convert.ToHexString(secret),
            Base32.Encode(secret),
            challengeToken,
            FirstDevice,
            SecondDevice,
            home.AccessToken,
            home.RefreshToken,
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!,
        };
        secrets.AddRange(recoveryCodes);
        secrets.AddRange(recoveryCodes.Select(code => code.Replace("-", string.Empty, StringComparison.Ordinal)));
        foreach (var integrationEvent in published)
        {
            var json = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType());
            foreach (var value in secrets)
            {
                json.ShouldNotContain(value, Case.Insensitive);
            }
        }
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

    private string CodeAt(byte[] secret) => Totp.ComputeCode(secret, Totp.TimeStepAt(Factory.Time.GetUtcNow()));

    /// <summary>Starts a TOTP setup for the user <see cref="IntegrationTestBase.Client"/> is signed in as; returns the method and its secret.</summary>
    private async Task<(Guid MethodId, byte[] Secret)> SetUpAuthenticatorAsync()
    {
        using var setup = await Client.PostAsJsonAsync(SetupRoute, new { }, Ct);
        setup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await setup.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("methodId").GetGuid(), Base32.Decode(body.GetProperty("secret").GetString()!));
    }

    /// <summary>Steps up the session whose token the client sends, and makes the client send the new token.</summary>
    private async Task StepUpAsync(object proof)
    {
        using var response = await Client.PostAsJsonAsync(StepUpRoute, proof, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString()!;
        AuthTestHarness.Authorize(Client, token);
    }

    /// <summary>Signs <see cref="Email"/> in on a device the way a client does, from its own address unless one is given.</summary>
    private async Task<Tokens> SignInOnDeviceAsync(string deviceName, string? userAgent = null, string? address = null)
    {
        using var response = await PostAnonymousAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password, deviceName }, address, userAgent);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return new Tokens(body.GetProperty("accessToken").GetString()!, body.GetProperty("refreshToken").GetString()!, body.GetProperty("sessionId").GetGuid());
    }

    /// <summary>The password step of an account with two-step verification; returns the challenge token.</summary>
    private async Task<string> ChallengeAsync(string deviceName)
    {
        using var response = await PostAnonymousAsync(LoginRoute, new { email = Email, password = AuthTestHarness.Password, deviceName });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("mfaRequired").GetBoolean().ShouldBeTrue();
        return body.GetProperty("challengeToken").GetString()!;
    }

    private async Task<HttpResponseMessage> PostAnonymousAsync(string route, object body, string? address = null, string? userAgent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
        request.Headers.Add(TestClientAddressStartupFilter.HeaderName, address ?? $"198.51.100.{Interlocked.Increment(ref _addressCount)}");
        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return await Client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) => Client.PostAsJsonAsync(RefreshRoute, new { refreshToken }, Ct);

    private Task<OutboxMessage> OutboxMessageAsync<TDomainEvent>()
        => QueryAsync(context => context.Set<OutboxMessage>().AsNoTracking().SingleAsync(message => message.Type == typeof(TDomainEvent).FullName, Ct));

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, Guid SessionId);
}
