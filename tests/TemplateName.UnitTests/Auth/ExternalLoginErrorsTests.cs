using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth;

public sealed class ExternalLoginErrorsTests
{
    public static TheoryData<Error, string, ErrorType> Errors => new()
    {
        { ExternalLoginErrors.ProviderNotFound, "auth.external_provider_not_found", ErrorType.NotFound },
        { ExternalLoginErrors.InvalidToken, "auth.invalid_external_token", ErrorType.Unauthorized },
        { ExternalLoginErrors.ProviderUnavailable, "auth.external_provider_unavailable", ErrorType.Unavailable },
        { ExternalLoginErrors.EmailUnverified, "auth.external_email_unverified", ErrorType.Forbidden },
        { ExternalLoginErrors.LinkRequired, "auth.external_link_required", ErrorType.Conflict },
        { ExternalLoginErrors.SignUpDisabled, "auth.external_sign_up_disabled", ErrorType.Forbidden },
        { ExternalLoginErrors.IdentityTaken, "auth.external_identity_taken", ErrorType.Conflict },
        { ExternalLoginErrors.ProviderAlreadyLinked, "auth.external_provider_already_linked", ErrorType.Conflict },
        { ExternalLoginErrors.LoginNotFound, "auth.external_login_not_found", ErrorType.NotFound },
        { ExternalLoginErrors.LastLoginMethod, "auth.last_login_method", ErrorType.Conflict },
        { ExternalLoginErrors.StepUpIdentityMismatch, "auth.step_up_identity_mismatch", ErrorType.Validation },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public void Each_error_has_the_code_and_type_of_the_spec(Error error, string code, ErrorType type)
    {
        error.Code.ShouldBe(code);
        error.Type.ShouldBe(type);
        error.Message.ShouldNotBeNullOrWhiteSpace();
    }
}
