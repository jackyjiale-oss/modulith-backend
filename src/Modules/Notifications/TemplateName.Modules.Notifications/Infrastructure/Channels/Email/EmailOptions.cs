using System.ComponentModel.DataAnnotations;
using System.Net;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

/// <summary>
/// The SMTP server and sender identity (section <c>Notifications:Email</c>; Plan 2's <c>Auth:Email</c> with the same keys, defaults and
/// ranges, D15). <see cref="Username"/> and <see cref="Password"/> are empty in the files; set them with user secrets or environment
/// variables. Start-up refuses a <see cref="Username"/> without <see cref="UseTls"/> unless <see cref="Host"/> is a loopback address
/// (<c>localhost</c>, <c>127.0.0.1</c>, <c>::1</c>), because the credentials would travel in clear.
/// </summary>
internal sealed class EmailOptions : IValidatableObject
{
    internal const string SectionName = "Notifications:Email";

    /// <summary>The SMTP host. The default is Mailpit from <c>docker-compose.yml</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    /// <summary>The SMTP port.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 1025;

    /// <summary>Whether to upgrade the connection with STARTTLS. Turn it on for every real server.</summary>
    public bool UseTls { get; set; }

    /// <summary>The account name. When empty the transport does not authenticate.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The account password. Secrets only, never in a file.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>The address the email comes from.</summary>
    [Required]
    [EmailAddress]
    public string From { get; set; } = "no-reply@localhost.test";

    /// <summary>The display name shown next to <see cref="From"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(100)]
    public string FromName { get; set; } = "TemplateName";

    /// <summary>How long connecting and sending may each take before the attempt fails.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The message names the keys only: never the host, the account or the password.
        if (Username.Length > 0 && !UseTls && !IsLoopback(Host))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(UseTls)} must be true when {SectionName}:{nameof(Username)} is set, unless {SectionName}:{nameof(Host)} "
                    + "is a loopback address (localhost, 127.0.0.1, ::1): without TLS the SMTP credentials travel in clear.",
                [nameof(UseTls)]);
        }
    }

    private static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
