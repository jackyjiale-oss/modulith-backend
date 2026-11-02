namespace TemplateName.SharedKernel;

/// <summary>Category of an expected failure; the web layer maps each value to an HTTP status.</summary>
public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    Failure,

    /// <summary>A dependency the request needs (an external service) cannot be reached right now; the caller may retry later.</summary>
    Unavailable,
}
