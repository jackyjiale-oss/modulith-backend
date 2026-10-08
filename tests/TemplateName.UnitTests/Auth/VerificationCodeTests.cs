using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class VerificationCodeTests
{
    private const string Target = "ALICE@EXAMPLE.COM";
    private const string ProtectedToken = "CfDJ8-protected-payload";

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static VerificationCode Issue(
        VerificationPurpose purpose = VerificationPurpose.EmailVerify,
        Guid? userId = null,
        string? createdIp = "203.0.113.7") =>
        VerificationCode.Issue(userId ?? Guid.NewGuid(), purpose, Target, Hash(1), ProtectedToken, Lifetime, createdIp, Now);

    [Fact]
    public void Issue_raises_event_with_protected_token_and_expiry()
    {
        var userId = Guid.NewGuid();

        var code = Issue(VerificationPurpose.PasswordReset, userId);

        code.Id.ShouldNotBe(Guid.Empty);
        code.UserId.ShouldBe(userId);
        code.Purpose.ShouldBe(VerificationPurpose.PasswordReset);
        code.Target.ShouldBe(Target);
        code.TokenHash.ShouldBe(Hash(1));
        code.CreatedAt.ShouldBe(Now);
        code.ExpiresAt.ShouldBe(Now + Lifetime);
        code.ConsumedAt.ShouldBeNull();
        code.InvalidatedAt.ShouldBeNull();
        code.CreatedIp.ShouldBe("203.0.113.7");
        code.IsPending(Now).ShouldBeTrue();
        code.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new VerificationCodeIssuedDomainEvent(code.Id, userId, VerificationPurpose.PasswordReset, Target, ProtectedToken));
    }

    [Fact]
    public void Issue_rejects_a_non_positive_lifetime()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.EmailVerify, Target, Hash(1), ProtectedToken, TimeSpan.Zero, null, Now));
    }

    [Fact]
    public void Consume_succeeds_once_then_fails()
    {
        var code = Issue();
        var later = Now.AddMinutes(5);

        var first = code.Consume(VerificationPurpose.EmailVerify, later);
        var second = code.Consume(VerificationPurpose.EmailVerify, later.AddSeconds(1));

        first.IsSuccess.ShouldBeTrue();
        code.ConsumedAt.ShouldBe(later);
        code.IsPending(later).ShouldBeFalse();
        second.IsFailure.ShouldBeTrue();
        second.Error.ShouldBe(VerificationErrors.InvalidToken);
        code.ConsumedAt.ShouldBe(later);
    }

    [Fact]
    public void Consume_at_exact_expiry_fails_and_one_tick_before_succeeds()
    {
        var atExpiry = Issue();
        var beforeExpiry = Issue();

        var failed = atExpiry.Consume(VerificationPurpose.EmailVerify, Now + Lifetime);
        var succeeded = beforeExpiry.Consume(VerificationPurpose.EmailVerify, Now + Lifetime - TimeSpan.FromTicks(1));

        failed.IsFailure.ShouldBeTrue();
        failed.Error.ShouldBe(VerificationErrors.InvalidToken);
        atExpiry.ConsumedAt.ShouldBeNull();
        atExpiry.IsPending(Now + Lifetime).ShouldBeFalse();
        succeeded.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Consume_with_other_purpose_fails()
    {
        var code = Issue(VerificationPurpose.PasswordReset);

        var result = code.Consume(VerificationPurpose.EmailVerify, Now.AddMinutes(1));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(VerificationErrors.InvalidToken);
        code.ConsumedAt.ShouldBeNull();
        code.IsPending(Now.AddMinutes(1)).ShouldBeTrue();
    }

    [Fact]
    public void Invalidate_makes_code_unusable_and_is_idempotent()
    {
        var code = Issue();
        var first = Now.AddMinutes(1);

        code.Invalidate(first);
        code.Invalidate(first.AddMinutes(1));
        var result = code.Consume(VerificationPurpose.EmailVerify, first.AddMinutes(2));

        code.InvalidatedAt.ShouldBe(first);
        code.IsPending(first).ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(VerificationErrors.InvalidToken);
    }

    [Fact]
    public void Invalidate_keeps_a_consumed_code_as_consumed()
    {
        var code = Issue();
        code.Consume(VerificationPurpose.EmailVerify, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();

        code.Invalidate(Now.AddMinutes(2));

        code.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public void Invalid_token_is_a_validation_error_with_the_expected_code()
    {
        VerificationErrors.InvalidToken.Code.ShouldBe("auth.invalid_token");
        VerificationErrors.InvalidToken.Type.ShouldBe(ErrorType.Validation);
    }
}
