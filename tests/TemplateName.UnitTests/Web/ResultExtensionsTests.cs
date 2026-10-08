using Microsoft.AspNetCore.Http.HttpResults;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;

namespace TemplateName.UnitTests.Web;

public sealed class ResultExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.NotFound, 404)]
    [InlineData(ErrorType.Conflict, 409)]
    [InlineData(ErrorType.Unauthorized, 401)]
    [InlineData(ErrorType.Forbidden, 403)]
    [InlineData(ErrorType.Failure, 500)]
    public void Maps_error_type_to_status_and_code(ErrorType type, int status)
    {
        var problem = Result.Failure(new Error("x.y", "msg", type)).ToProblem().ShouldBeOfType<ProblemHttpResult>();

        problem.StatusCode.ShouldBe(status);
        problem.ProblemDetails.Detail.ShouldBe("msg");
        problem.ProblemDetails.Extensions["code"].ShouldBe("x.y");
    }

    [Fact]
    public void Parameters_map_to_params_extension()
    {
        var id = Guid.NewGuid();
        var error = Error.NotFound("x.missing", "msg") with { Parameters = new Dictionary<string, object?> { ["id"] = id } };

        var problem = Result.Failure(error).ToProblem().ShouldBeOfType<ProblemHttpResult>();

        problem.ProblemDetails.Extensions["params"].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["id"].ShouldBe(id);
    }

    [Fact]
    public void Error_without_parameters_has_no_params_extension()
    {
        var problem = Result.Failure(Error.NotFound("x.missing", "msg")).ToProblem().ShouldBeOfType<ProblemHttpResult>();

        problem.ProblemDetails.Extensions.ContainsKey("params").ShouldBeFalse();
    }

    [Fact]
    public void Plain_validation_error_maps_to_400_with_code()
    {
        var problem = Result.Failure(Error.Validation("x.invalid", "msg")).ToProblem().ShouldBeOfType<ProblemHttpResult>();

        problem.StatusCode.ShouldBe(400);
        problem.ProblemDetails.Extensions["code"].ShouldBe("x.invalid");
    }

    [Fact]
    public void Validation_error_maps_to_400_with_errors()
    {
        var error = new ValidationError(new Dictionary<string, string[]> { ["reason"] = ["required"] });

        var problem = Result.Failure(error).ToProblem().ShouldBeOfType<ValidationProblem>();

        problem.StatusCode.ShouldBe(400);
        problem.ProblemDetails.Errors["reason"].ShouldBe(["required"]);
        problem.ProblemDetails.Extensions["code"].ShouldBe("validation.failed");
    }

    [Fact]
    public void ToProblem_on_success_throws()
        => Should.Throw<InvalidOperationException>(() => Result.Success().ToProblem());
}
