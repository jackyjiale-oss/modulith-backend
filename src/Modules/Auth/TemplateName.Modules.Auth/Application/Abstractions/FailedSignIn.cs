namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>The user's sign-in state right after <see cref="IUserRepository.RecordFailedSignInAsync"/> counted a failure.</summary>
/// <param name="AccessFailedCount">The failures counted so far, this one included.</param>
/// <param name="LockoutEnd">Set only when this failure locked the account (the count was skipped while a lockout lasted).</param>
internal sealed record FailedSignIn(int AccessFailedCount, DateTimeOffset? LockoutEnd)
{
    /// <summary>True when this failure reached the threshold and locked the account.</summary>
    public bool LockedOut => LockoutEnd is not null;
}
