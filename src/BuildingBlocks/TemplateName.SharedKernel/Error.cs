namespace TemplateName.SharedKernel;

/// <summary>
/// An expected failure. Codes are <c>module.snake_case</c> (for example <c>leave.not_pending</c>). <see cref="Message"/> is the English
/// default; the HTTP boundary replaces it with the translation of <see cref="Code"/> for the caller's language.
/// </summary>
public record Error(string Code, string Message, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    /// <summary>
    /// Values for the named placeholders of the translated message (<c>{id}</c>), returned to clients as <c>params</c>, so never put
    /// secrets or personal data here. Records compare this dictionary by reference: compare errors that carry parameters by
    /// <see cref="Code"/>.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
