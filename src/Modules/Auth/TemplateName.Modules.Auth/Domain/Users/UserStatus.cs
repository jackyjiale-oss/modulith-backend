namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>The life-cycle state of a user account. The numeric values are persisted, so a new state is appended, never inserted or renumbered.</summary>
internal enum UserStatus
{
    /// <summary>The account can sign in.</summary>
    Active = 0,

    /// <summary>An administrator has suspended the account; it cannot sign in.</summary>
    Suspended = 1,
}
