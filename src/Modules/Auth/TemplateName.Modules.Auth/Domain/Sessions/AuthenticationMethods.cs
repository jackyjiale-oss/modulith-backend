namespace TemplateName.Modules.Auth.Domain.Sessions;

/// <summary>
/// The <c>amr</c> values (RFC 8176) a sign-in can carry and the format <see cref="UserSession.AuthMethods"/> stores them in: values
/// separated by single spaces. The access token carries the same values as a JSON array.
/// </summary>
internal static class AuthenticationMethods
{
    /// <summary>A password.</summary>
    public const string Password = "pwd";

    /// <summary>A one-time password from an authenticator app.</summary>
    public const string OneTimePassword = "otp";

    /// <summary>A recovery code.</summary>
    public const string RecoveryCode = "rcvr";

    /// <summary>More than one factor was used.</summary>
    public const string MultiFactor = "mfa";

    /// <summary>Joins <paramref name="methods"/> with single spaces, the <see cref="UserSession.AuthMethods"/> format.</summary>
    /// <exception cref="ArgumentException">No method is given, or a method is empty or contains a space.</exception>
    public static string Join(params string[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        if (methods.Length == 0)
        {
            throw new ArgumentException("At least one authentication method is required.", nameof(methods));
        }

        foreach (var method in methods)
        {
            if (string.IsNullOrEmpty(method) || method.Contains(' '))
            {
                throw new ArgumentException("An authentication method is not empty and contains no space.", nameof(methods));
            }
        }

        return string.Join(' ', methods);
    }
}
