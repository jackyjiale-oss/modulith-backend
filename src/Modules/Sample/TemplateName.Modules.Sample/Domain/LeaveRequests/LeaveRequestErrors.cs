using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Domain.LeaveRequests;

/// <summary>The module's errors. Each code has a message in <c>Resources/SampleErrorMessages.resx</c> and its translations.</summary>
internal static class LeaveRequestErrors
{
    public static readonly Error InvalidDateRange = Error.Validation(
        "leave.invalid_date_range",
        "The end date must not be before the start date.");

    public static readonly Error NotPending = Error.Conflict(
        "leave.not_pending",
        "Only a pending leave request can be approved.");

    public static Error NotFound(Guid id) =>
        Error.NotFound("leave.not_found", $"Leave request '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
