using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.IntegrationEvents;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Auth.IntegrationEvents;

/// <summary>
/// <c>PublishVerificationCodeIssuedDomainEventHandler</c> over a real Data Protection protector (ephemeral keys), so the published link
/// is real ciphertext and the tests can decrypt it as a consumer would.
/// </summary>
public sealed class VerificationCodeIssuedPublisherTests
{
    private const string Token = "a+token/with=specials";

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MessageOccurredAt = new(2026, 10, 9, 8, 59, 30, TimeSpan.Zero);
    private static readonly Guid MessageId = new("0199c8a0-0000-7000-8000-000000000001");
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly IVerificationCodeRepository _verificationCodes = Substitute.For<IVerificationCodeRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IOutboxMessageContext _messageContext = Substitute.For<IOutboxMessageContext>();
    private readonly DataProtectionSecretProtector _secretProtector = new(new EphemeralDataProtectionProvider());
    private readonly RecordingIntegrationEventPublisher _publisher = new();
    private readonly RecordingLogger<PublishVerificationCodeIssuedDomainEventHandler> _logger = new();
    private readonly LinksOptions _links = new()
    {
        ConfirmEmailUrl = "https://app.example.com/confirm?token={token}",
        ResetPasswordUrl = "https://app.example.com/reset?token={token}",
    };

    private readonly User _user = User.Register("Alice@Example.com", "Alice", "ms", "hash", Now.AddDays(-1)).Value;

    public VerificationCodeIssuedPublisherTests()
    {
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _messageContext.MessageId.Returns(MessageId);
        _messageContext.OccurredAt.Returns(MessageOccurredAt);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Verification_publishes_email_verification_with_protected_link_and_expiry()
    {
        var (code, issued) = IssueCode(VerificationPurpose.EmailVerify);

        await Handler(_publisher).HandleAsync(issued, Ct);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<EmailVerificationRequestedIntegrationEvent>();
        published.UserId.ShouldBe(_user.Id);
        published.Email.ShouldBe("Alice@Example.com");
        published.ExpiresAt.ShouldBe(code.ExpiresAt);
        var link = _links.ConfirmEmailLink(Token);
        _secretProtector.Unprotect(published.ProtectedActionUrl).ShouldBe(link);
        published.ProtectedActionUrl.ShouldNotBe(link);
        published.ProtectedActionUrl.ShouldNotContain(Uri.EscapeDataString(Token));
        published.ProtectedActionUrl.ShouldNotContain("app.example.com");
    }

    [Theory]
    [InlineData(nameof(VerificationTrigger.SelfService), PasswordResetReason.SelfService)]
    [InlineData(nameof(VerificationTrigger.CreatedByAdmin), PasswordResetReason.CreatedByAdmin)]
    [InlineData(nameof(VerificationTrigger.ForcedByAdmin), PasswordResetReason.ForcedByAdmin)]
    public async Task Reset_maps_each_trigger_to_its_reason(string trigger, PasswordResetReason expectedReason)
    {
        var (code, issued) = IssueCode(VerificationPurpose.PasswordReset, Enum.Parse<VerificationTrigger>(trigger));

        await Handler(_publisher).HandleAsync(issued, Ct);

        var published = _publisher.Published.ShouldHaveSingleItem().ShouldBeOfType<PasswordResetRequestedIntegrationEvent>();
        published.Reason.ShouldBe(expectedReason);
        published.UserId.ShouldBe(_user.Id);
        published.Email.ShouldBe("Alice@Example.com");
        published.ExpiresAt.ShouldBe(code.ExpiresAt);
        _secretProtector.Unprotect(published.ProtectedActionUrl).ShouldBe(_links.ResetPasswordLink(Token));
    }

    [Theory]
    [InlineData("superseded")]
    [InlineData("consumed")]
    [InlineData("expired")]
    [InlineData("unknown")]
    public async Task Superseded_or_consumed_code_publishes_nothing(string state)
    {
        var (code, issued) = IssueCode(VerificationPurpose.PasswordReset, issuedAt: state == "expired" ? Now - Lifetime : Now.AddMinutes(-1));
        switch (state)
        {
            case "superseded":
                code.Invalidate(Now.AddSeconds(-10));
                break;
            case "consumed":
                code.Consume(VerificationPurpose.PasswordReset, Now.AddSeconds(-10)).IsSuccess.ShouldBeTrue();
                break;
            case "unknown":
                _verificationCodes.GetByIdAsync(code.Id, Arg.Any<CancellationToken>()).Returns((VerificationCode?)null);
                break;
        }

        await Handler(_publisher).HandleAsync(issued, Ct);

        _publisher.Published.ShouldBeEmpty();
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        var entry = _logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldContain(code.Id.ToString());
    }

    [Fact]
    public async Task Changed_address_publishes_nothing()
    {
        var (code, issued) = IssueCode(VerificationPurpose.EmailVerify, target: "OLD@EXAMPLE.COM");

        await Handler(_publisher).HandleAsync(issued, Ct);

        _publisher.Published.ShouldBeEmpty();
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain(code.Id.ToString());
    }

    [Fact]
    public async Task Missing_user_publishes_nothing()
    {
        var (code, issued) = IssueCode(VerificationPurpose.PasswordReset, userId: Guid.NewGuid());

        await Handler(_publisher).HandleAsync(issued, Ct);

        _publisher.Published.ShouldBeEmpty();
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain(code.Id.ToString());
    }

    [Fact]
    public async Task Event_id_and_time_come_from_the_outbox_message()
    {
        var (_, verification) = IssueCode(VerificationPurpose.EmailVerify);
        var (_, reset) = IssueCode(VerificationPurpose.PasswordReset);
        var handler = Handler(_publisher);

        await handler.HandleAsync(verification, Ct);
        await handler.HandleAsync(reset, Ct);

        _publisher.Published.Count.ShouldBe(2);
        _publisher.Published.ShouldAllBe(published => published.Id == MessageId && published.OccurredAt == MessageOccurredAt);
    }

    [Fact]
    public async Task Publisher_failure_is_not_swallowed()
    {
        var (_, issued) = IssueCode(VerificationPurpose.EmailVerify);
        var failingPublisher = Substitute.For<IIntegrationEventPublisher>();
        failingPublisher.PublishAsync(Arg.Any<EmailVerificationRequestedIntegrationEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("A consumer failed.")));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Handler(failingPublisher).HandleAsync(issued, Ct));

        exception.Message.ShouldBe("A consumer failed.");
    }

    [Fact]
    public async Task Nothing_logged_contains_the_token_link_or_address()
    {
        // Every path of the handler, all into the one recording logger: both purposes published, then each reason to skip.
        var handler = Handler(_publisher);
        await handler.HandleAsync(IssueCode(VerificationPurpose.EmailVerify).Issued, Ct);
        await handler.HandleAsync(IssueCode(VerificationPurpose.PasswordReset, VerificationTrigger.ForcedByAdmin).Issued, Ct);
        var (superseded, supersededIssued) = IssueCode(VerificationPurpose.PasswordReset);
        superseded.Invalidate(Now);
        await handler.HandleAsync(supersededIssued, Ct);
        await handler.HandleAsync(IssueCode(VerificationPurpose.EmailVerify, target: "OLD@EXAMPLE.COM").Issued, Ct);
        await handler.HandleAsync(IssueCode(VerificationPurpose.EmailVerify, userId: Guid.NewGuid()).Issued, Ct);

        _publisher.Published.Count.ShouldBe(2);
        _logger.Entries.Count.ShouldBe(5);
        var secrets = new List<string>
        {
            Token,
            Uri.EscapeDataString(Token),
            _links.ConfirmEmailLink(Token),
            _links.ResetPasswordLink(Token),
        };
        secrets.AddRange(_publisher.Published.Select(published => published switch
        {
            EmailVerificationRequestedIntegrationEvent verification => verification.ProtectedActionUrl,
            PasswordResetRequestedIntegrationEvent reset => reset.ProtectedActionUrl,
            _ => throw new InvalidOperationException("Unexpected event."),
        }));
        foreach (var (level, message, properties) in _logger.Entries)
        {
            level.ShouldBe(LogLevel.Information);
            var text = message + " " + string.Join(' ', properties.Values);
            foreach (var secret in secrets)
            {
                text.ShouldNotContain(secret);
            }

            text.ShouldNotContain("example.com", Case.Insensitive);
            text.ShouldNotContain("alice", Case.Insensitive);
        }
    }

    private PublishVerificationCodeIssuedDomainEventHandler Handler(IIntegrationEventPublisher publisher)
        => new(_verificationCodes, _users, _secretProtector, publisher, _messageContext, Options.Create(_links), new FakeTimeProvider(Now), _logger);

    /// <summary>Issues a code the way the application does (its event carries the protected token) and makes the repository find it.</summary>
    private (VerificationCode Code, VerificationCodeIssuedDomainEvent Issued) IssueCode(
        VerificationPurpose purpose,
        VerificationTrigger trigger = VerificationTrigger.SelfService,
        string? target = null,
        Guid? userId = null,
        DateTimeOffset? issuedAt = null)
    {
        var code = VerificationCode.Issue(
            userId ?? _user.Id,
            purpose,
            trigger,
            target ?? _user.NormalizedEmail,
            new byte[32],
            _secretProtector.Protect(Token),
            Lifetime,
            createdIp: null,
            issuedAt ?? Now.AddMinutes(-1));
        _verificationCodes.GetByIdAsync(code.Id, Arg.Any<CancellationToken>()).Returns(code);

        return (code, code.DomainEvents.OfType<VerificationCodeIssuedDomainEvent>().Single());
    }
}
