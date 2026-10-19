using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>An authenticator app the harness enrolled for a user (see <see cref="IntegrationTestBase"/>).</summary>
/// <param name="MethodId">The confirmed <c>UserMfaMethod</c>; its <c>LastUsedTimeStep</c> is null, so any current code is accepted once.</param>
/// <param name="Secret">The plain TOTP secret (20 bytes); only its protected form is stored.</param>
/// <param name="RecoveryCodes">The ten recovery codes as shown to the user (<c>XXXX-XXXX-XXXX-XXXX</c>); only their hashes are stored.</param>
public sealed record TotpEnrollment(Guid MethodId, byte[] Secret, IReadOnlyList<string> RecoveryCodes)
{
    /// <summary>The 6-digit code an authenticator app shows at <paramref name="instant"/>.</summary>
    public string CodeAt(DateTimeOffset instant) => Totp.ComputeCode(Secret, Totp.TimeStepAt(instant));
}
