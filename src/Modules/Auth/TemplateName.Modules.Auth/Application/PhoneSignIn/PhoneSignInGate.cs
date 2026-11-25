using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Application.PhoneSignIn;

/// <summary>
/// The privileged-role rule of phone sign-in: a member of a role in <see cref="PhoneOptions.SignInDeniedRoles"/> may not sign in with a
/// texted code alone. With a confirmed TOTP method the challenge supplies a second factor, so the member is not denied.
/// </summary>
internal sealed class PhoneSignInGate(IRoleRepository roles, IMfaMethodRepository mfaMethods, IOptions<PhoneOptions> options)
{
    public async Task<bool> IsDeniedAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var denied = options.Value.DeniedRoleSet();
        if (denied.Count == 0 || user.Roles.Count == 0)
        {
            return false;
        }

        var held = await roles.GetByIdsAsync([.. user.Roles.Select(role => role.RoleId)], cancellationToken);
        if (!held.Any(role => denied.Contains(role.NormalizedName)))
        {
            return false;
        }

        return !await mfaMethods.HasConfirmedAsync(user.Id, cancellationToken);
    }
}
