namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// What a secret template variable holds, which decides how the scheduler checks its decrypted value. Declared in code, never stored;
/// the numbers are kept stable all the same.
/// </summary>
internal enum SecretKind : byte
{
    /// <summary>A single-use link: an absolute <c>http</c> or <c>https</c> URL with a host, no user info, at most 2048 characters.</summary>
    Link = 1,

    /// <summary>A one-time code: 4 to 10 ASCII digits.</summary>
    Code = 2,
}
