using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Mfa.GetMethods;

/// <summary>Reads the signed-in user's two-step verification: whether it is on, the unused recovery codes left and the methods.</summary>
internal sealed record GetMfaMethodsQuery : IQuery<MfaMethodsResponse>;
