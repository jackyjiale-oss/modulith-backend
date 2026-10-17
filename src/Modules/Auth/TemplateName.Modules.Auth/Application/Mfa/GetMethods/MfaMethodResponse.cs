using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Mfa.GetMethods;

/// <summary>One method of the caller, without its secret. The instants are UTC.</summary>
/// <param name="Id">The method's id.</param>
/// <param name="Type">The kind of factor (<c>Totp</c>).</param>
/// <param name="Label">The name the user gave it.</param>
/// <param name="Confirmed">False while the setup is pending: a pending method protects nothing.</param>
/// <param name="CreatedAt">When the setup started or last restarted.</param>
/// <param name="ConfirmedAt">When a code confirmed it; null while pending.</param>
/// <param name="LastUsedAt">When a code from it was last accepted; null until then.</param>
internal sealed record MfaMethodResponse(
    Guid Id,
    MfaMethodType Type,
    string Label,
    bool Confirmed,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    DateTime? LastUsedAt);
