using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class UserMfaMethodTests
{
    private const string Secret = "CfDJ8-protected-secret";

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartTotp_is_pending_and_Confirm_records_the_time_step()
    {
        var userId = Guid.NewGuid();

        var method = UserMfaMethod.StartTotp(userId, "Authenticator app", Secret, Now);

        method.Id.ShouldNotBe(Guid.Empty);
        method.UserId.ShouldBe(userId);
        method.Type.ShouldBe(MfaMethodType.Totp);
        method.Label.ShouldBe("Authenticator app");
        method.SecretEncrypted.ShouldBe(Secret);
        method.CreatedAt.ShouldBe(Now);
        method.IsConfirmed.ShouldBeFalse();
        method.ConfirmedAt.ShouldBeNull();
        method.LastUsedAt.ShouldBeNull();
        method.LastUsedTimeStep.ShouldBeNull();
        method.DomainEvents.ShouldBeEmpty();

        var later = Now.AddMinutes(2);
        var result = method.Confirm(58_666_667, later);

        result.IsSuccess.ShouldBeTrue();
        method.IsConfirmed.ShouldBeTrue();
        method.ConfirmedAt.ShouldBe(later);
        method.LastUsedAt.ShouldBe(later);
        method.LastUsedTimeStep.ShouldBe(58_666_667);
    }

    [Fact]
    public void Confirm_twice_is_already_enabled()
    {
        var method = UserMfaMethod.StartTotp(Guid.NewGuid(), "Authenticator app", Secret, Now);
        method.Confirm(100, Now).IsSuccess.ShouldBeTrue();

        var result = method.Confirm(200, Now.AddMinutes(5));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(MfaErrors.AlreadyEnabled);
        method.ConfirmedAt.ShouldBe(Now);
        method.LastUsedAt.ShouldBe(Now);
        method.LastUsedTimeStep.ShouldBe(100);
    }

    [Fact]
    public void RestartSetup_replaces_the_secret_only_while_pending()
    {
        var method = UserMfaMethod.StartTotp(Guid.NewGuid(), "Old phone", Secret, Now);
        var restartedAt = Now.AddMinutes(10);

        method.RestartSetup("New phone", "CfDJ8-other-secret", restartedAt);

        method.Label.ShouldBe("New phone");
        method.SecretEncrypted.ShouldBe("CfDJ8-other-secret");
        method.CreatedAt.ShouldBe(restartedAt);
        method.IsConfirmed.ShouldBeFalse();

        method.Confirm(10, restartedAt).IsSuccess.ShouldBeTrue();

        Should.Throw<InvalidOperationException>(() => method.RestartSetup("Third phone", "CfDJ8-third", restartedAt.AddMinutes(1)));
        method.Label.ShouldBe("New phone");
        method.SecretEncrypted.ShouldBe("CfDJ8-other-secret");
    }

    [Fact]
    public void Confirm_refuses_a_negative_time_step_and_stays_pending()
    {
        var method = UserMfaMethod.StartTotp(Guid.NewGuid(), "Authenticator app", Secret, Now);

        Should.Throw<ArgumentOutOfRangeException>(() => method.Confirm(-1, Now));

        method.IsConfirmed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StartTotp_refuses_a_blank_label_or_secret(string blank)
    {
        Should.Throw<ArgumentException>(() => UserMfaMethod.StartTotp(Guid.NewGuid(), blank, Secret, Now));
        Should.Throw<ArgumentException>(() => UserMfaMethod.StartTotp(Guid.NewGuid(), "Authenticator app", blank, Now));
    }

    [Fact]
    public void StartTotp_and_RestartSetup_refuse_a_label_or_secret_longer_than_the_column()
    {
        var tooLongLabel = new string('l', UserMfaMethod.MaxLabelLength + 1);
        var tooLongSecret = new string('s', UserMfaMethod.MaxSecretEncryptedLength + 1);

        Should.Throw<ArgumentException>(() => UserMfaMethod.StartTotp(Guid.NewGuid(), tooLongLabel, Secret, Now));
        Should.Throw<ArgumentException>(() => UserMfaMethod.StartTotp(Guid.NewGuid(), "Authenticator app", tooLongSecret, Now));

        var method = UserMfaMethod.StartTotp(
            Guid.NewGuid(),
            new string('l', UserMfaMethod.MaxLabelLength),
            new string('s', UserMfaMethod.MaxSecretEncryptedLength),
            Now);

        Should.Throw<ArgumentException>(() => method.RestartSetup(tooLongLabel, Secret, Now));
        Should.Throw<ArgumentException>(() => method.RestartSetup("Authenticator app", tooLongSecret, Now));
        method.Label.Length.ShouldBe(UserMfaMethod.MaxLabelLength);
    }

    [Fact]
    public void RecoveryCode_Create_stores_the_hash_unused()
    {
        var userId = Guid.NewGuid();
        var hash = Hash(7);

        var code = RecoveryCode.Create(userId, hash, Now);

        code.Id.ShouldNotBe(Guid.Empty);
        code.UserId.ShouldBe(userId);
        code.CodeHash.ShouldBe(hash);
        code.CreatedAt.ShouldBe(Now);
        code.UsedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void RecoveryCode_Create_refuses_a_hash_that_is_not_32_bytes(int length)
    {
        Should.Throw<ArgumentException>(() => RecoveryCode.Create(Guid.NewGuid(), new byte[length], Now));
    }

    [Fact]
    public void Mfa_enum_values_are_stable()
    {
        ((int)MfaMethodType.Totp).ShouldBe(1);
        ((int)MfaChange.Enabled).ShouldBe(0);
        ((int)MfaChange.Disabled).ShouldBe(1);
        ((int)MfaChange.RecoveryCodesRegenerated).ShouldBe(2);
        ((int)MfaChange.ResetByAdmin).ShouldBe(3);
        ((int)MfaChange.RecoveryCodeUsed).ShouldBe(4);
        Enum.GetValues<MfaChange>().Length.ShouldBe(5);
    }

    [Fact]
    public void Mfa_errors_have_the_expected_codes_types_and_messages()
    {
        Check(MfaErrors.InvalidChallenge, "auth.invalid_mfa_challenge", ErrorType.Validation, "The sign-in attempt is invalid or has expired. Sign in again.");
        Check(MfaErrors.InvalidCode, "auth.invalid_mfa_code", ErrorType.Validation, "The verification code is incorrect.");
        Check(MfaErrors.AlreadyEnabled, "auth.mfa_already_enabled", ErrorType.Conflict, "Two-step verification is already turned on.");
        Check(MfaErrors.NotEnabled, "auth.mfa_not_enabled", ErrorType.Conflict, "Two-step verification is not turned on.");
        Check(
            MfaErrors.SetupNotPending,
            "auth.mfa_setup_not_pending",
            ErrorType.Conflict,
            "There is no authenticator setup to confirm. Start the setup again.");
        Check(MfaErrors.StepUpRequired, "auth.step_up_required", ErrorType.Forbidden, "Confirm your identity again to continue.");
        Check(
            MfaErrors.StepUpMethodNotAllowed,
            "auth.step_up_method_not_allowed",
            ErrorType.Validation,
            "This way of confirming your identity is not available for your account.");

        var id = Guid.NewGuid();
        var notFound = MfaErrors.MethodNotFound(id);
        Check(notFound, "auth.mfa_method_not_found", ErrorType.NotFound, $"Verification method '{id}' was not found.");
        notFound.Parameters.ShouldNotBeNull().ShouldContainKeyAndValue("id", id);
    }

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static void Check(Error error, string code, ErrorType type, string message)
    {
        error.Code.ShouldBe(code);
        error.Type.ShouldBe(type);
        error.Message.ShouldBe(message);
    }
}
