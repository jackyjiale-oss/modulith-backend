namespace TemplateName.Modules.Auth.Contracts.IntegrationEvents;

/// <summary>
/// How a user's verified phone number changed. Serialized by its numeric value: a new kind is appended and existing values are never
/// renumbered.
/// </summary>
public enum PhoneNumberChangeKind
{
    /// <summary>A first number was verified on the account.</summary>
    Added = 1,

    /// <summary>Another number was verified in place of the previous one.</summary>
    Replaced = 2,

    /// <summary>The number was removed from the account.</summary>
    Removed = 3,
}
