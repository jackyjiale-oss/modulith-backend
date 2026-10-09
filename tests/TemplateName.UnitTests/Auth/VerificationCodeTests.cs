using System.Text.Json;
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
        VerificationCode.Issue(userId ?? Guid.NewGuid(), purpose, VerificationTrigger.SelfService, Target, Hash(1), ProtectedToken, Lifetime, createdIp, Now);

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
            .ShouldBe(new VerificationCodeIssuedDomainEvent(
                code.Id, userId, VerificationPurpose.PasswordReset, Target, ProtectedToken, VerificationTrigger.SelfService));
    }

    [Theory]
    [InlineData(nameof(VerificationTrigger.CreatedByAdmin))]
    [InlineData(nameof(VerificationTrigger.ForcedByAdmin))]
    public void Issue_carries_the_trigger_of_a_reset_in_its_event(string trigger)
    {
        var expected = Enum.Parse<VerificationTrigger>(trigger);

        var code = VerificationCode.Issue(
            Guid.NewGuid(), VerificationPurpose.PasswordReset, expected, Target, Hash(1), ProtectedToken, Lifetime, null, Now);

        code.DomainEvents.OfType<VerificationCodeIssuedDomainEvent>().ShouldHaveSingleItem().Trigger.ShouldBe(expected);
    }

    [Theory]
    [InlineData(nameof(VerificationTrigger.CreatedByAdmin))]
    [InlineData(nameof(VerificationTrigger.ForcedByAdmin))]
    public void Issue_rejects_an_administrator_trigger_for_an_email_confirmation(string trigger)
    {
        Should.Throw<ArgumentException>(() => VerificationCode.Issue(
            Guid.NewGuid(), VerificationPurpose.EmailVerify, Enum.Parse<VerificationTrigger>(trigger), Target, Hash(1), ProtectedToken, Lifetime, null, Now));
    }

    [Fact]
    public void Issued_event_saved_before_the_trigger_existed_reads_as_self_service()
    {
        // An outbox row written before the trigger was added has no Trigger member; the dispatcher reads it with the same call.
        var codeId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var content = $$"""{"CodeId":"{{codeId}}","UserId":"{{userId}}","Purpose":2,"Target":"{{Target}}","ProtectedToken":"{{ProtectedToken}}"}""";

        var issued = JsonSerializer.Deserialize(content, typeof(VerificationCodeIssuedDomainEvent));

        issued.ShouldBe(new VerificationCodeIssuedDomainEvent(codeId, userId, VerificationPurpose.PasswordReset, Target, ProtectedToken, VerificationTrigger.SelfService));
    }

    [Fact]
    public void Issued_event_round_trips_its_trigger_through_the_outbox_serializer()
    {
        var issued = new VerificationCodeIssuedDomainEvent(
            Guid.NewGuid(), Guid.NewGuid(), VerificationPurpose.PasswordReset, Target, ProtectedToken, VerificationTrigger.ForcedByAdmin);

        var content = JsonSerializer.Serialize(issued, typeof(VerificationCodeIssuedDomainEvent));

        JsonSerializer.Deserialize(content, typeof(VerificationCodeIssuedDomainEvent)).ShouldBe(issued);
    }

    [Fact]
    public void Issue_rejects_a_non_positive_lifetime()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            VerificationCode.Issue(Guid.NewGuid(), VerificationPurpose.EmailVerify, VerificationTrigger.SelfService, Target, Hash(1), ProtectedToken, TimeSpan.Zero, null, Now));
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
    public void CanConsume_answers_like_Consume_without_changing_the_code()
    {
        var code = Issue();

        code.CanConsume(VerificationPurpose.EmailVerify, Now).ShouldBeTrue();
        code.CanConsume(VerificationPurpose.PasswordReset, Now).ShouldBeFalse();
        code.CanConsume(VerificationPurpose.EmailVerify, Now + Lifetime).ShouldBeFalse();
        code.ConsumedAt.ShouldBeNull();

        code.Consume(VerificationPurpose.EmailVerify, Now).IsSuccess.ShouldBeTrue();
        code.CanConsume(VerificationPurpose.EmailVerify, Now).ShouldBeFalse();

        var invalidated = Issue();
        invalidated.Invalidate(Now);
        invalidated.CanConsume(VerificationPurpose.EmailVerify, Now).ShouldBeFalse();
    }

    [Fact]
    public void Invalid_token_is_a_validation_error_with_the_expected_code()
    {
        VerificationErrors.InvalidToken.Code.ShouldBe("auth.invalid_token");
        VerificationErrors.InvalidToken.Type.ShouldBe(ErrorType.Validation);
    }

    [Fact]
    public void Issue_cuts_created_ip_to_its_column_length()
    {
        var code = Issue(createdIp: new string('i', 100));

        code.CreatedIp.ShouldBe(new string('i', VerificationCode.MaxCreatedIpLength));
    }
}
