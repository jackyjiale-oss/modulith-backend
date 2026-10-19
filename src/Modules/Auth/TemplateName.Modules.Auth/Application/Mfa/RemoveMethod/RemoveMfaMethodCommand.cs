using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Auth.Application.Mfa.RemoveMethod;

/// <summary>Removes the signed-in user's verification method <paramref name="MethodId"/>: cancels a pending setup, or turns two-step verification off.</summary>
internal sealed record RemoveMfaMethodCommand(Guid MethodId) : ICommand;
