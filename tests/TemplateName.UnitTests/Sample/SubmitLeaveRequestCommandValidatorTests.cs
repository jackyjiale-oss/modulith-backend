using FluentValidation.TestHelper;
using TemplateName.Modules.Sample.Application.LeaveRequests.Submit;

namespace TemplateName.UnitTests.Sample;

public sealed class SubmitLeaveRequestCommandValidatorTests
{
    private readonly SubmitLeaveRequestCommandValidator _sut = new();

    [Fact]
    public void Valid_command_passes()
    {
        var result = _sut.TestValidate(ValidCommand());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_employee_id_fails()
    {
        var result = _sut.TestValidate(ValidCommand() with { EmployeeId = Guid.Empty });

        result.ShouldHaveValidationErrorFor(command => command.EmployeeId);
    }

    [Fact]
    public void Empty_reason_fails()
    {
        var result = _sut.TestValidate(ValidCommand() with { Reason = string.Empty });

        result.ShouldHaveValidationErrorFor(command => command.Reason);
    }

    [Fact]
    public void Reason_longer_than_500_characters_fails()
    {
        var result = _sut.TestValidate(ValidCommand() with { Reason = new string('x', 501) });

        result.ShouldHaveValidationErrorFor(command => command.Reason);
    }

    [Fact]
    public void Reason_of_exactly_500_characters_passes()
    {
        var result = _sut.TestValidate(ValidCommand() with { Reason = new string('x', 500) });

        result.ShouldNotHaveValidationErrorFor(command => command.Reason);
    }

    [Fact]
    public void End_date_before_start_date_fails()
    {
        var result = _sut.TestValidate(ValidCommand() with { EndDate = new DateOnly(2026, 3, 1) });

        result.ShouldHaveValidationErrorFor(command => command.EndDate);
    }

    private static SubmitLeaveRequestCommand ValidCommand()
        => new(Guid.NewGuid(), new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 4), "Trip");
}
