using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// Issues a user's whole set of recovery codes (plan decision D5). The set is replaced, never extended, so a code never needs a batch id:
/// the old codes and the new ones are staged in the caller's unit of work, whose single save commits both together with the user's
/// stamp change (two simultaneous replacements conflict on the user's <c>RowVersion</c>).
/// </summary>
internal sealed class RecoveryCodeIssuer(IRecoveryCodeRepository codes)
{
    /// <summary>
    /// Stages the removal of every existing code of the user, used or not, and <see cref="RecoveryCodeFormat.Count"/> new ones, of which
    /// only the hash (<see cref="RecoveryCodeFormat.Hash"/>) is stored, and returns the new codes as shown to the user
    /// (<c>XXXX-XXXX-XXXX-XXXX</c>). The caller shows them once and never logs, audits or raises them; nothing is saved here.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReplaceAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var existing in await codes.ListByUserAsync(userId, cancellationToken))
        {
            codes.Remove(existing);
        }

        var shown = new List<string>(RecoveryCodeFormat.Count);
        for (var index = 0; index < RecoveryCodeFormat.Count; index++)
        {
            var code = RecoveryCodeFormat.Generate();
            codes.Add(RecoveryCode.Create(userId, RecoveryCodeFormat.Hash(code), now));
            shown.Add(RecoveryCodeFormat.Display(code));
        }

        return shown.AsReadOnly();
    }
}
