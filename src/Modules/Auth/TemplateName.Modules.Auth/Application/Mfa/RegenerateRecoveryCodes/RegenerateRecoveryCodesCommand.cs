using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Mfa.RegenerateRecoveryCodes;

/// <summary>Replaces the signed-in user's whole set of recovery codes with ten new ones, shown once in the answer.</summary>
internal sealed record RegenerateRecoveryCodesCommand : ICommand<RecoveryCodesResponse>;
