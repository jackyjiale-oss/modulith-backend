using TemplateName.SharedKernel;
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

    /// <param name="unitOfWork">Saves the module changes.</param>
    /// <param name="stageChangesAsync">
    /// Reads the current rows and stages the request changes on them; called once per attempt. A failure (an <c>If-Match</c> that no
    /// longer holds against the fresh read, ADR 0038) ends the loop at once with that failure and nothing saved.
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static async Task<Result> SaveAsync(
        IUnitOfWork unitOfWork, Func<CancellationToken, Task<Result>> stageChangesAsync, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var staged = await stageChangesAsync(cancellationToken);
            if (staged.IsFailure)
            {
                return staged;
            }

            if (attempt == MaxAttempts)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }

            if (await unitOfWork.SaveChangesUnlessConflictAsync(cancellationToken))
            {
                return Result.Success();
            }
        }
    }

    /// <summary>The same loop for a caller whose staging cannot fail (the delivery retry): it stages and saves as before.</summary>
    public static Task SaveAsync(IUnitOfWork unitOfWork, Func<CancellationToken, Task> stageChangesAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stageChangesAsync);
        return SaveAsync(
            unitOfWork,
            async token =>
            {
                await stageChangesAsync(token);
                return Result.Success();
            },
            cancellationToken);
    }
}
