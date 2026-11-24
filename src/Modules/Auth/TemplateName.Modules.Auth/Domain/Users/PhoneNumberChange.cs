namespace TemplateName.Modules.Auth.Domain.Users;

/// <summary>How the account's verified phone number changed.</summary>
internal enum PhoneNumberChange
{
    Added = 1,
    Replaced = 2,
    Removed = 3,
}
