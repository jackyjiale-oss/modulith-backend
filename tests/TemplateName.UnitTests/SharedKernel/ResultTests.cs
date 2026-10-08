using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.SharedKernel;

public sealed class ResultTests
{
    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBe(Error.None);
    }

    [Fact]
    public void Failure_carries_error()
    {
        var error = Error.NotFound("leave.not_found", "x");

        var result = Result.Failure(error);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
    }

    [Fact]
    public void Failure_with_none_throws()
        => Should.Throw<ArgumentException>(() => Result.Failure(Error.None));

    [Fact]
    public void Value_of_failed_result_throws()
        => Should.Throw<InvalidOperationException>(() => _ = Result.Failure<int>(Error.Conflict("a.b", "c")).Value);

    [Fact]
    public void Implicit_conversions_create_success_and_failure()
    {
        Result<int> success = 5;
        Result<int> failure = Error.Failure("a.b", "c");

        success.Value.ShouldBe(5);
        failure.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void ValidationError_has_validation_type_and_code()
    {
        var validationError = new ValidationError(new Dictionary<string, string[]> { ["reason"] = ["required"] });

        validationError.Type.ShouldBe(ErrorType.Validation);
        validationError.Code.ShouldBe("validation.failed");
        validationError.Errors["reason"].ShouldBe(["required"]);
    }
}
