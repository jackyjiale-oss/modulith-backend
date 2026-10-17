using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class MfaChangeEffectsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly User _user;
    private readonly MfaChangeEffects _sut;

    public MfaChangeEffectsTests()
    {
        _user = User.Register("alice@example.com", "Alice", "en", "stored-hash", Now.AddDays(-1)).Value;
        _user.ClearDomainEvents();
        _sut = new MfaChangeEffects(_sessions);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MfaChangeEffects_revokes_all_but_the_kept_session_with_the_given_reason()
    {
        var oldStamp = _user.SecurityStamp;
        var other = StartSession();
        var kept = StartSession();
        var third = StartSession();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other, kept, third]);

        var revoked = await _sut.ApplyAsync(_user, MfaChange.Enabled, kept.Id, SessionRevokedReason.MfaChanged, Now, Ct);

        revoked.ShouldBe(2);
        _user.SecurityStamp.ShouldNotBe(oldStamp);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.Enabled));
        foreach (var session in new[] { other, third })
        {
            session.RevokedAt.ShouldBe(Now);
            session.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
            session.SecurityStamp.ShouldBe(oldStamp);
        }

        // The kept session takes the new stamp, so its next refresh is not refused as a stamp mismatch.
        kept.RevokedAt.ShouldBeNull();
        kept.SecurityStamp.ShouldBe(_user.SecurityStamp);
    }

    [Theory]
    [InlineData((int)SessionRevokedReason.MfaChanged)]
    [InlineData((int)SessionRevokedReason.AdminRevoked)]
    public async Task Without_a_kept_session_every_session_is_revoked(int reasonValue)
    {
        var reason = (SessionRevokedReason)reasonValue;
        var first = StartSession();
        var second = StartSession();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([first, second]);

        var revoked = await _sut.ApplyAsync(_user, MfaChange.ResetByAdmin, keepSessionId: null, reason, Now, Ct);

        revoked.ShouldBe(2);
        first.RevokedReason.ShouldBe(reason);
        second.RevokedReason.ShouldBe(reason);
        _user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MfaChangedDomainEvent(_user.Id, MfaChange.ResetByAdmin));
    }

    [Fact]
    public async Task Kept_session_that_is_not_active_is_not_revived()
    {
        // The caller's session ended meanwhile: it is not among the active ones, so nothing adopts the new stamp and nothing is counted.
        var other = StartSession();
        _sessions.GetActiveByUserAsync(_user.Id, Now, Arg.Any<CancellationToken>()).Returns([other]);

        var revoked = await _sut.ApplyAsync(_user, MfaChange.Disabled, Guid.NewGuid(), SessionRevokedReason.MfaChanged, Now, Ct);

        revoked.ShouldBe(1);
        other.RevokedReason.ShouldBe(SessionRevokedReason.MfaChanged);
    }

    private UserSession StartSession()
    {
        var (session, _) = UserSession.Start(
            _user.Id,
            AuthenticationMethods.Password,
            "Phone",
            userAgent: null,
            ipAddress: null,
            _user.SecurityStamp,
            [.. Enumerable.Range(1, 32).Select(value => (byte)value)],
            TimeSpan.FromDays(14),
            TimeSpan.FromDays(90),
            Now.AddHours(-1));
        return session;
    }
}
