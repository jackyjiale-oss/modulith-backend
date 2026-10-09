using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Me.GetMe;

/// <summary>Reads the signed-in user's profile, roles and permissions.</summary>
internal sealed record GetMeQuery : IQuery<MeResponse>;
