using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Infrastructure.Email;

/// <summary>
/// The SMTP server and sender identity (section <c>Auth:Email</c>). <see cref="Username"/> and <see cref="Password"/> are empty in the
/// files; set them with user secrets or environment variables.
/// </summary>
internal sealed class EmailOptions
{
    internal const string SectionName = "Auth:Email";

    /// <summary>The SMTP host. The default is Mailpit from <c>docker-compose.yml</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = "localhost";

    /// <summary>The SMTP port.</summary>
    [Range(1, 65535)]
    public int Port { get; set; } = 1025;

    /// <summary>Whether to upgrade the connection with STARTTLS. Turn it on for every real server.</summary>
    public bool UseTls { get; set; }

    /// <summary>The account name. When empty the sender does not authenticate.</summary>
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
}
