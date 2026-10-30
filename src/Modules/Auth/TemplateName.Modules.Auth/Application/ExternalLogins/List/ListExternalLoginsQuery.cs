using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.ExternalLogins.List;

/// <summary>Reads the signed-in user's external logins and whether the user has a password.</summary>
internal sealed record ListExternalLoginsQuery : IQuery<ExternalLoginsResponse>;
