using System.Reflection;
using TemplateName.Modules.Auth.Domain.Audit;

namespace TemplateName.UnitTests.Auth;

public sealed class AuthAuditLogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_sets_the_given_values_and_leaves_client_info_empty()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var entry = AuthAuditLog.Create(
            AuthAuditEvents.LoginFailed,
            succeeded: false,
            Now,
            userId,
            failureReason: "auth.invalid_credentials",
            attemptedIdentifier: "a****@example.com",
            sessionId: sessionId,
            details: """{"attempt":3}""");

        entry.Id.ShouldBe(0);
        entry.OccurredAt.ShouldBe(Now);
        entry.UserId.ShouldBe(userId);
        entry.EventType.ShouldBe(AuthAuditEvents.LoginFailed);
        entry.Succeeded.ShouldBeFalse();
        entry.FailureReason.ShouldBe("auth.invalid_credentials");
        entry.AttemptedIdentifier.ShouldBe("a****@example.com");
        entry.SessionId.ShouldBe(sessionId);
        entry.Details.ShouldBe("""{"attempt":3}""");
        entry.IpAddress.ShouldBeNull();
        entry.UserAgent.ShouldBeNull();
        entry.TraceId.ShouldBeNull();
    }

    [Fact]
    public void Create_trims_overlong_identifier()
    {
        var entry = AuthAuditLog.Create(AuthAuditEvents.LoginFailed, false, Now, attemptedIdentifier: new string('x', 300));

        entry.AttemptedIdentifier.ShouldBe(new string('x', 256));
    }

    [Fact]
    public void Create_keeps_an_identifier_at_the_limit_and_does_not_validate_details()
    {
        var identifier = new string('y', 256);

        var entry = AuthAuditLog.Create(AuthAuditEvents.Registered, true, Now, attemptedIdentifier: identifier, details: "not json at all");

        entry.AttemptedIdentifier.ShouldBe(identifier);
        entry.Details.ShouldBe("not json at all");
    }

    [Fact]
    public void SetClientInfo_stores_the_values()
    {
        var entry = AuthAuditLog.Create(AuthAuditEvents.LoginSucceeded, true, Now);

        entry.SetClientInfo("203.0.113.7", "Mozilla/5.0", "00-abc-def-01");

        entry.IpAddress.ShouldBe("203.0.113.7");
        entry.UserAgent.ShouldBe("Mozilla/5.0");
        entry.TraceId.ShouldBe("00-abc-def-01");
    }

    [Fact]
    public void SetClientInfo_trims_values_to_the_column_limits()
    {
        var entry = AuthAuditLog.Create(AuthAuditEvents.LoginSucceeded, true, Now);

        entry.SetClientInfo(new string('1', 60), new string('u', 600), new string('t', 80));

        entry.IpAddress.ShouldBe(new string('1', 45));
        entry.UserAgent.ShouldBe(new string('u', 512));
        entry.TraceId.ShouldBe(new string('t', 64));
    }

    [Fact]
    public void SetClientInfo_accepts_nulls()
    {
        var entry = AuthAuditLog.Create(AuthAuditEvents.LoginSucceeded, true, Now);

        entry.SetClientInfo(null, null, null);

        entry.IpAddress.ShouldBeNull();
        entry.UserAgent.ShouldBeNull();
        entry.TraceId.ShouldBeNull();
    }

    [Fact]
    public void MaskIdentifier_keeps_first_character_and_domain_of_an_email()
    {
        AuthAuditLog.MaskIdentifier("alice@example.com").ShouldBe("a****@example.com");
    }

    [Fact]
    public void MaskIdentifier_masks_a_short_local_part_without_revealing_its_length()
    {
        AuthAuditLog.MaskIdentifier("a@example.com").ShouldBe("a****@example.com");
        AuthAuditLog.MaskIdentifier("ab@example.com").ShouldBe("a****@example.com");
    }

    [Fact]
    public void MaskIdentifier_without_at_sign_keeps_first_character_only()
    {
        AuthAuditLog.MaskIdentifier("alice").ShouldBe("a****");
    }

    [Fact]
    public void MaskIdentifier_returns_null_for_null_or_empty()
    {
        AuthAuditLog.MaskIdentifier(null).ShouldBeNull();
        AuthAuditLog.MaskIdentifier(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void MaskIdentifier_returns_null_for_whitespace_and_trims_surrounding_space()
    {
        AuthAuditLog.MaskIdentifier("   ").ShouldBeNull();
        AuthAuditLog.MaskIdentifier("  alice@example.com ").ShouldBe("a****@example.com");
    }

    [Fact]
    public void Audit_event_names_are_unique_non_empty_strings()
    {
        var values = typeof(AuthAuditEvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.Count.ShouldBe(30);
        values.ShouldAllBe(v => !string.IsNullOrWhiteSpace(v));
        values.Distinct().Count().ShouldBe(values.Count);
    }
}
