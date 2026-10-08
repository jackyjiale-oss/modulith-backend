using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Authentication.Refresh;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class RefreshTokenCommandHandlerTests
{
    private const string PresentedToken = "presented-refresh-token-of-forty-three-xxxx";
    private const string NewTokenValue = "replacement-refresh-token-forty-three-xxxxx";
    private const string AccessTokenValue = "header.payload.signature";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = Now.AddHours(-1);
    private static readonly TimeSpan Sliding = TimeSpan.FromDays(14);
    private static readonly TimeSpan Absolute = TimeSpan.FromDays(90);
    private static readonly byte[] PresentedHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];
    private static readonly byte[] NewHash = [.. Enumerable.Range(101, 32).Select(value => (byte)value)];

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly IAuthAuditWriter _auditWriter = Substitute.For<IAuthAuditWriter>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuthMetrics _metrics = Substitute.For<IAuthMetrics>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly List<AuthAuditLog> _auditEntries = [];
    private readonly List<AccessTokenRequest> _tokenRequests = [];
    private readonly User _user;
    private readonly UserSession _session;
    private readonly RefreshToken _presented;
    private readonly RefreshTokenCommandHandler _sut;

    public RefreshTokenCommandHandlerTests()
    {
        _user = User.Register("alice@example.com", "Alice", "ms", "stored-hash", Now.AddDays(-2)).Value;
        _user.ConfirmEmail(Now.AddDays(-2));
        _user.ClearDomainEvents();
        (_session, _presented) = UserSession.Start(
            _user.Id, "pwd", "Pixel 9", "UnitTests/1.0", "203.0.113.7", _user.SecurityStamp, PresentedHash, Sliding, Absolute, SignedInAt);

        _tokenService.Hash(PresentedToken).Returns(PresentedHash);
        _tokenService.Generate().Returns(new GeneratedToken(NewTokenValue, NewHash));
        _sessions.GetByRefreshTokenHashAsync(PresentedHash, Arg.Any<CancellationToken>()).Returns(_session);
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _sessions.TryClaimRefreshTokenAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        _accessTokenIssuer.Issue(Arg.Do<AccessTokenRequest>(_tokenRequests.Add)).Returns(new AccessToken(AccessTokenValue, Now.AddMinutes(10)));
        _auditWriter.Record(Arg.Do<AuthAuditLog>(_auditEntries.Add));

        _sut = new RefreshTokenCommandHandler(
            _sessions,
            _users,
            _tokenService,
            _accessTokenIssuer,
            _auditWriter,
            _unitOfWork,
            _metrics,
            Options.Create(new RefreshTokenOptions()),
            _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RefreshTokenCommand Command() => new(PresentedToken);

    [Fact]
    public async Task Refresh_claims_the_token_before_rotating_and_issues_a_new_pair()
    {
        var tokensAtClaim = -1;
        _sessions.TryClaimRefreshTokenAsync(_presented.Id, Now, CancellationToken.None)
            .Returns(_ =>
            {
                tokensAtClaim = _session.RefreshTokens.Count;
                return true;
            });

        var result = await _sut.HandleAsync(Command(), Ct);

        var response = result.Value;
        response.AccessToken.ShouldBe(AccessTokenValue);
        response.AccessTokenExpiresAt.ShouldBe(Now.AddMinutes(10));
        response.RefreshToken.ShouldBe(NewTokenValue);
        response.RefreshTokenExpiresAt.ShouldBe(Now + Sliding);
        response.SessionId.ShouldBe(_session.Id);

        // The claim ran on the loaded instance before Rotate added the replacement (Ruling R7).
        tokensAtClaim.ShouldBe(1);
        await _sessions.Received(1).TryClaimRefreshTokenAsync(_presented.Id, Now, CancellationToken.None);
        _session.RefreshTokens.Count.ShouldBe(2);
        var replacement = _session.RefreshTokens.Single(token => token.Id != _presented.Id);
        replacement.TokenHash.ShouldBe(NewHash);
        _presented.UsedAt.ShouldBe(Now);
        _presented.ReplacedByTokenId.ShouldBe(replacement.Id);
        _session.LastSeenAt.ShouldBe(Now);
        _session.RevokedAt.ShouldBeNull();

        // amr and auth_time come from the session (the sign-in), not from this refresh.
        _tokenRequests.ShouldHaveSingleItem().ShouldBe(
            new AccessTokenRequest(_user.Id, _session.Id, _session.SecurityStamp, "pwd", SignedInAt, "ms"));

        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.TokenRefreshed);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        _metrics.DidNotReceive().RecordTokenReuse();
    }

    [Fact]
    public async Task Claim_failure_is_handled_as_reuse()
    {
        _sessions.TryClaimRefreshTokenAsync(_presented.Id, Now, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(SessionErrors.RefreshTokenReused);
        _session.RevokedAt.ShouldBe(Now);
        _session.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);
        _session.RefreshTokens.ShouldHaveSingleItem().RevokedAt.ShouldBe(Now);
        _presented.UsedAt.ShouldBeNull();
        _session.DomainEvents.OfType<RefreshTokenReuseDetectedDomainEvent>().ShouldHaveSingleItem()
            .ShouldBe(new RefreshTokenReuseDetectedDomainEvent(_user.Id, _session.Id));
        AssertReuseAudited();
        _metrics.Received(1).RecordTokenReuse();
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        _tokenService.DidNotReceive().Generate();
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
    }

    [Fact]
    public async Task Reuse_audits_and_counts_and_revokes_the_session()
    {
        // The presented token was already exchanged for a newer one.
        var newer = _session.Rotate(PresentedHash, [.. Enumerable.Repeat((byte)7, 32)], _user.SecurityStamp, Sliding, Now.AddMinutes(-5)).Value;

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(SessionErrors.RefreshTokenReused);
        _session.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);
        _session.RevokedAt.ShouldBe(Now);
        newer.RevokedAt.ShouldBe(Now);
        _presented.RevokedAt.ShouldBe(Now);
        _session.DomainEvents.OfType<RefreshTokenReuseDetectedDomainEvent>().ShouldHaveSingleItem();
        AssertReuseAudited();
        _metrics.Received(1).RecordTokenReuse();
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);

        // Detected on the loaded chain, so nothing is claimed.
        await _sessions.DidNotReceiveWithAnyArgs().TryClaimRefreshTokenAsync(default, default, Ct);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
    }

    [Fact]
    public async Task Stamp_mismatch_revokes_and_fails()
    {
        _user.ChangePassword("new-hash", historyCount: 5, Now.AddMinutes(-1));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        _session.RevokedAt.ShouldBe(Now);
        _session.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        _presented.RevokedAt.ShouldBe(Now);
        _presented.UsedAt.ShouldBeNull();
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RefreshFailed);
        audit.Succeeded.ShouldBeFalse();
        audit.FailureReason.ShouldBe(RefreshTokenCommandHandler.StampChangedFailureReason);
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        await _sessions.DidNotReceiveWithAnyArgs().TryClaimRefreshTokenAsync(default, default, Ct);
        _metrics.DidNotReceive().RecordTokenReuse();
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
    }

    [Fact]
    public async Task Unknown_token_fails_as_invalid_without_a_claim()
    {
        _sessions.GetByRefreshTokenHashAsync(PresentedHash, Arg.Any<CancellationToken>()).Returns((UserSession?)null);

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RefreshFailed);
        audit.UserId.ShouldBeNull();
        audit.SessionId.ShouldBeNull();
        audit.FailureReason.ShouldBe("auth.invalid_refresh_token");
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        await _users.DidNotReceiveWithAnyArgs().GetByIdAsync(default, Ct);
        await _sessions.DidNotReceiveWithAnyArgs().TryClaimRefreshTokenAsync(default, default, Ct);
    }

    [Fact]
    public async Task Token_of_a_revoked_session_fails_as_invalid_and_is_not_called_reuse()
    {
        _session.Revoke(SessionRevokedReason.Logout, Now.AddMinutes(-1));

        var result = await _sut.HandleAsync(Command(), Ct);

        result.Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        _session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        _session.DomainEvents.ShouldBeEmpty();
        _auditEntries.ShouldHaveSingleItem().FailureReason.ShouldBe("auth.invalid_refresh_token");
        _metrics.DidNotReceive().RecordTokenReuse();
        await _sessions.DidNotReceiveWithAnyArgs().TryClaimRefreshTokenAsync(default, default, Ct);
    }

    [Fact]
    public async Task Expired_token_fails_without_a_claim_so_a_second_try_is_not_reuse()
    {
        _time.SetUtcNow(SignedInAt + Sliding);

        var first = await _sut.HandleAsync(Command(), Ct);
        var second = await _sut.HandleAsync(Command(), Ct);

        first.Error.ShouldBe(SessionErrors.RefreshTokenExpired);
        second.Error.ShouldBe(SessionErrors.RefreshTokenExpired);
        _session.RevokedAt.ShouldBeNull();
        _presented.UsedAt.ShouldBeNull();
        _auditEntries.Select(entry => entry.FailureReason).ShouldBe(["auth.refresh_token_expired", "auth.refresh_token_expired"]);
        await _sessions.DidNotReceiveWithAnyArgs().TryClaimRefreshTokenAsync(default, default, Ct);
    }

    [Fact]
    public async Task Missing_user_revokes_the_session_and_fails()
    {
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.HandleAsync(Command(), Ct);

        await AssertAccountUnavailableAsync(result.Error);
    }

    [Fact]
    public async Task Suspended_user_revokes_the_session_and_fails()
    {
        _user.Suspend(Now.AddMinutes(-1));

        var result = await _sut.HandleAsync(Command(), Ct);

        await AssertAccountUnavailableAsync(result.Error);
    }

    [Fact]
    public void Command_text_never_shows_the_token()
    {
        Command().ToString().ShouldNotContain(PresentedToken);
    }

    private async Task AssertAccountUnavailableAsync(Error error)
    {
        error.ShouldBe(SessionErrors.InvalidRefreshToken);
        _session.RevokedAt.ShouldBe(Now);
        _session.RevokedReason.ShouldBe(SessionRevokedReason.AdminRevoked);
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RefreshFailed);
        audit.FailureReason.ShouldBe(RefreshTokenCommandHandler.AccountUnavailableFailureReason);
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(CancellationToken.None);
        await _sessions.DidNotReceiveWithAnyArgs().TryClaimRefreshTokenAsync(default, default, Ct);
        _accessTokenIssuer.DidNotReceiveWithAnyArgs().Issue(default!);
    }

    private void AssertReuseAudited()
    {
        var audit = _auditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.TokenReuseDetected);
        audit.Succeeded.ShouldBeFalse();
        audit.UserId.ShouldBe(_user.Id);
        audit.SessionId.ShouldBe(_session.Id);
        audit.FailureReason.ShouldBe("auth.refresh_token_reused");
    }
}
