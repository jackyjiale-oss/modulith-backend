namespace TemplateName.SharedKernel;

/// <summary>A validation failure that carries the field-level messages, keyed by property name.</summary>
public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors)
    : Error("validation.failed", "One or more validation errors occurred.", ErrorType.Validation);
