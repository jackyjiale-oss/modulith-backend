using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Application.Preferences;

/// <summary>
/// Saves a user's own settings when several requests of the same user may change the same rows at once: two of them creating one row
/// (the key is the user, type and channel), or one changing or deleting a row the other already changed. The first attempts use
/// <see cref="IUnitOfWork.SaveChangesUnlessConflictAsync"/>; on a conflict the context has dropped its pending changes, so the changes
/// are staged again from fresh reads and saved again. The last attempt saves plainly, so a conflict that still remains is thrown
/// (an optimistic-concurrency one becomes 409 <c>concurrency.conflict</c> at the HTTP boundary).
/// </summary>
internal static class ConflictRetry
{
    /// <summary>The attempts made in all, the last of them a plain save.</summary>
    public const int MaxAttempts = 3;

    /// <param name="unitOfWork">Saves the module's changes.</param>
    /// <param name="stageChangesAsync">Reads the current rows and stages the request's changes on them; called once per attempt.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static async Task SaveAsync(IUnitOfWork unitOfWork, Func<CancellationToken, Task> stageChangesAsync, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await stageChangesAsync(cancellationToken);

            if (attempt == MaxAttempts)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            if (await unitOfWork.SaveChangesUnlessConflictAsync(cancellationToken))
            {
                return;
            }
        }
    }
}
