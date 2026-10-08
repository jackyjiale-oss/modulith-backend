using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class UserSessionTests
{
    private const string Stamp = "stamp-1";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Sliding = TimeSpan.FromDays(14);
    private static readonly TimeSpan Absolute = TimeSpan.FromDays(90);

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static (UserSession Session, RefreshToken First) StartSession(Guid? userId = null) =>
        UserSession.Start(userId ?? Guid.NewGuid(), "pwd", "Pixel 9", "Mozilla/5.0", "203.0.113.7", Stamp, Hash(1), Sliding, Absolute, Now);

    [Fact]
    public void Start_creates_active_session_with_first_token_expiring_at_sliding_lifetime()
    {
        var userId = Guid.NewGuid();

        var (session, first) = StartSession(userId);

        session.Id.ShouldNotBe(Guid.Empty);
        session.UserId.ShouldBe(userId);
        session.AuthMethods.ShouldBe("pwd");
        session.DeviceName.ShouldBe("Pixel 9");
        session.UserAgent.ShouldBe("Mozilla/5.0");
        session.IpAddress.ShouldBe("203.0.113.7");
        session.SecurityStamp.ShouldBe(Stamp);
        session.CreatedAt.ShouldBe(Now);
        session.LastSeenAt.ShouldBe(Now);
        session.ExpiresAt.ShouldBe(Now + Absolute);
        session.RevokedAt.ShouldBeNull();
        session.RevokedReason.ShouldBeNull();
        session.IsActive(Now).ShouldBeTrue();
        session.IsActive(Now + Absolute).ShouldBeFalse();
        session.RefreshTokens.ShouldHaveSingleItem().ShouldBe(first);
        first.SessionId.ShouldBe(session.Id);
        first.TokenHash.ShouldBe(Hash(1));
        first.CreatedAt.ShouldBe(Now);
        first.ExpiresAt.ShouldBe(Now + Sliding);
        first.UsedAt.ShouldBeNull();
        first.ReplacedByTokenId.ShouldBeNull();
        first.RevokedAt.ShouldBeNull();
        session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Start_first_token_never_outlives_the_session()
    {
        var (session, first) = UserSession.Start(Guid.NewGuid(), "pwd", "Laptop", null, null, Stamp, Hash(1), TimeSpan.FromDays(30), TimeSpan.FromDays(7), Now);

        first.ExpiresAt.ShouldBe(session.ExpiresAt);
    }

    [Fact]
    public void Rotate_marks_old_token_used_and_issues_replacement()
    {
        var (session, first) = StartSession();
        var later = Now.AddMinutes(30);

        var result = session.Rotate(Hash(1), Hash(2), Stamp, Sliding, later);

        result.IsSuccess.ShouldBeTrue();
        var replacement = result.Value;
        replacement.ShouldNotBeSameAs(first);
        replacement.SessionId.ShouldBe(session.Id);
        replacement.TokenHash.ShouldBe(Hash(2));
        replacement.CreatedAt.ShouldBe(later);
        replacement.ExpiresAt.ShouldBe(later + Sliding);
        replacement.UsedAt.ShouldBeNull();
        first.UsedAt.ShouldBe(later);
        first.ReplacedByTokenId.ShouldBe(replacement.Id);
        first.RevokedAt.ShouldBeNull();
        session.LastSeenAt.ShouldBe(later);
        session.RefreshTokens.Count.ShouldBe(2);
        session.IsActive(later).ShouldBeTrue();
        session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Rotate_with_used_token_revokes_session_and_raises_reuse_event()
    {
        var (session, first) = StartSession();
        var replacement = session.Rotate(Hash(1), Hash(2), Stamp, Sliding, Now.AddMinutes(1)).Value;
        var later = Now.AddMinutes(2);

        var result = session.Rotate(Hash(1), Hash(3), Stamp, Sliding, later);

        result.Error.ShouldBe(SessionErrors.RefreshTokenReused);
        session.RevokedAt.ShouldBe(later);
        session.RevokedReason.ShouldBe(SessionRevokedReason.TokenReuse);
        session.IsActive(later).ShouldBeFalse();
        replacement.RevokedAt.ShouldBe(later);
        first.RevokedAt.ShouldBe(later);
        session.RefreshTokens.Count.ShouldBe(2);
        session.DomainEvents.OfType<RefreshTokenReuseDetectedDomainEvent>().ShouldHaveSingleItem()
            .ShouldBe(new RefreshTokenReuseDetectedDomainEvent(session.UserId, session.Id));

        // The replacement no longer works either: the session is revoked.
        session.Rotate(Hash(2), Hash(4), Stamp, Sliding, later).Error.ShouldBe(SessionErrors.InvalidRefreshToken);
    }

    [Fact]
    public void Rotate_with_unknown_hash_fails_without_side_effects()
    {
        var (session, first) = StartSession();

        var result = session.Rotate(Hash(9), Hash(2), Stamp, Sliding, Now.AddMinutes(5));

        result.Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        session.RefreshTokens.ShouldHaveSingleItem().ShouldBe(first);
        first.UsedAt.ShouldBeNull();
        first.RevokedAt.ShouldBeNull();
        first.ReplacedByTokenId.ShouldBeNull();
        session.RevokedAt.ShouldBeNull();
        session.LastSeenAt.ShouldBe(Now);
        session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Rotate_after_token_expiry_fails_as_expired()
    {
        var (session, first) = StartSession();
        var expired = Now + Sliding;

        var result = session.Rotate(Hash(1), Hash(2), Stamp, Sliding, expired);

        result.Error.ShouldBe(SessionErrors.RefreshTokenExpired);
        first.UsedAt.ShouldBeNull();
        session.RefreshTokens.Count.ShouldBe(1);
        session.RevokedAt.ShouldBeNull();
        session.LastSeenAt.ShouldBe(Now);
    }

    [Fact]
    public void Rotate_after_session_expiry_fails_as_expired()
    {
        var (session, _) = StartSession();
        var token = session.Rotate(Hash(1), Hash(2), Stamp, TimeSpan.FromDays(100), Now.AddDays(1)).Value;
        token.ExpiresAt.ShouldBe(session.ExpiresAt);

        var result = session.Rotate(Hash(2), Hash(3), Stamp, Sliding, session.ExpiresAt);

        result.Error.ShouldBe(SessionErrors.RefreshTokenExpired);
        session.RefreshTokens.Count.ShouldBe(2);
    }

    [Fact]
    public void Rotate_new_token_never_outlives_the_session()
    {
        var (session, _) = StartSession();
        var nearEnd = Now + Absolute - TimeSpan.FromDays(2);

        // A sliding lifetime longer than the session is capped at the session's absolute expiry.
        var token = session.Rotate(Hash(1), Hash(2), Stamp, TimeSpan.FromDays(100), Now.AddDays(1)).Value;
        var tail = session.Rotate(Hash(2), Hash(3), Stamp, Sliding, nearEnd).Value;

        token.ExpiresAt.ShouldBe(session.ExpiresAt);
        tail.ExpiresAt.ShouldBe(session.ExpiresAt);
        tail.ExpiresAt.ShouldBeLessThan(nearEnd + Sliding);
    }

    [Fact]
    public void Rotate_with_changed_security_stamp_revokes_session()
    {
        var (session, first) = StartSession();
        var later = Now.AddMinutes(10);

        var result = session.Rotate(Hash(1), Hash(2), "stamp-2", Sliding, later);

        result.Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        session.RevokedAt.ShouldBe(later);
        session.RevokedReason.ShouldBe(SessionRevokedReason.PasswordChanged);
        first.RevokedAt.ShouldBe(later);
        first.UsedAt.ShouldBeNull();
        session.RefreshTokens.Count.ShouldBe(1);
        session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Rotate_on_revoked_session_fails()
    {
        var (session, first) = StartSession();
        session.Revoke(SessionRevokedReason.Logout, Now.AddMinutes(1));

        var result = session.Rotate(Hash(1), Hash(2), Stamp, Sliding, Now.AddMinutes(2));

        result.Error.ShouldBe(SessionErrors.InvalidRefreshToken);
        session.RefreshTokens.ShouldHaveSingleItem().ShouldBe(first);
        first.UsedAt.ShouldBeNull();
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Revoke_is_idempotent_and_keeps_the_first_reason()
    {
        var (session, _) = StartSession();
        session.Rotate(Hash(1), Hash(2), Stamp, Sliding, Now.AddMinutes(1));
        var first = Now.AddMinutes(5);

        session.Revoke(SessionRevokedReason.Logout, first);
        session.Revoke(SessionRevokedReason.AdminRevoked, Now.AddMinutes(9));

        session.RevokedAt.ShouldBe(first);
        session.RevokedReason.ShouldBe(SessionRevokedReason.Logout);
        session.IsActive(first).ShouldBeFalse();
        session.RefreshTokens.ShouldAllBe(token => token.RevokedAt == first);
    }

    [Fact]
    public void Session_errors_have_the_expected_codes_and_types()
    {
        SessionErrors.InvalidRefreshToken.Code.ShouldBe("auth.invalid_refresh_token");
        SessionErrors.InvalidRefreshToken.Type.ShouldBe(ErrorType.Unauthorized);
        SessionErrors.RefreshTokenExpired.Code.ShouldBe("auth.refresh_token_expired");
        SessionErrors.RefreshTokenExpired.Type.ShouldBe(ErrorType.Unauthorized);
        SessionErrors.RefreshTokenReused.Code.ShouldBe("auth.refresh_token_reused");
        SessionErrors.RefreshTokenReused.Type.ShouldBe(ErrorType.Unauthorized);

        var id = Guid.NewGuid();
        var notFound = SessionErrors.NotFound(id);
        notFound.Code.ShouldBe("auth.session_not_found");
        notFound.Type.ShouldBe(ErrorType.NotFound);
        notFound.Parameters!["id"].ShouldBe(id);
    }
}
