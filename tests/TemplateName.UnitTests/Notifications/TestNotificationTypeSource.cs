using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// Notification types for the renderer and completeness-checker tests. Their templates are embedded in this test assembly from
/// <c>Notifications/Templates/{Email,InApp}/</c> under the names a module's own <c>Templates</c> folder gets. Only
/// <see cref="Welcome"/> is complete; the others are deliberately partial or broken, so the production completeness tests never read
/// this source.
/// </summary>
internal sealed class TestNotificationTypeSource : INotificationTypeSource
{
    /// <summary>Complete: email and in-app parts in every culture; <c>action_url</c> is secret and used only in the email text and HTML.</summary>
    public const string Welcome = "test.welcome";

    /// <summary>In-app templates in <c>en</c> only.</summary>
    public const string EnglishOnly = "test.english_only";

    /// <summary>In-app templates in <c>ms</c> only, so a culture without templates has no English fallback.</summary>
    public const string NoEnglish = "test.no_english";

    /// <summary>A title that computes numbers and a body that prints a date passed as text.</summary>
    public const string Formatting = "test.formatting";

    /// <summary>A title that does not parse.</summary>
    public const string ParseError = "test.parse_error";

    /// <summary>In-app templates that break every per-type completeness rule.</summary>
    public const string Broken = "test.broken";

    /// <summary>A title that includes another file.</summary>
    public const string IncludeAttempt = "test.include_attempt";

    /// <summary>A title that imports a name.</summary>
    public const string ImportAttempt = "test.import_attempt";

    /// <summary>A title that calls Scriban's built-in <c>date</c> and <c>object</c> functions.</summary>
    public const string BuiltinCall = "test.builtin_call";

    /// <summary>A title that reads a .NET property of a variable.</summary>
    public const string MemberAccess = "test.member_access";

    /// <summary>A title that reads a secret through <c>this</c>, the whole model, without naming it.</summary>
    public const string ThisAccess = "test.this_access";

    /// <summary>A title with a loop of 100,000 iterations.</summary>
    public const string RunawayLoop = "test.runaway_loop";

    /// <summary>A title with a function that calls itself without end.</summary>
    public const string RunawayRecursion = "test.runaway_recursion";

    public IReadOnlyCollection<NotificationTypeDefinition> Types { get; } =
    [
        Define(Welcome, [NotificationChannel.Email, NotificationChannel.InApp], ["display_name", "occurred_at"], ["action_url"]),
        Define(EnglishOnly, [NotificationChannel.InApp], ["display_name"]),
        Define(NoEnglish, [NotificationChannel.InApp]),
        Define(Formatting, [NotificationChannel.InApp], ["occurred_at"]),
        Define(ParseError, [NotificationChannel.InApp], ["display_name"]),
        Define(Broken, [NotificationChannel.InApp], ["display_name"], ["action_url"]),
        Define(IncludeAttempt, [NotificationChannel.InApp], ["display_name"]),
        Define(ImportAttempt, [NotificationChannel.InApp], ["display_name"]),
        Define(BuiltinCall, [NotificationChannel.InApp], ["display_name"]),
        Define(MemberAccess, [NotificationChannel.InApp], ["display_name"]),
        Define(ThisAccess, [NotificationChannel.InApp], ["display_name"], ["action_url"]),
        Define(RunawayLoop, [NotificationChannel.InApp], ["display_name"]),
        Define(RunawayRecursion, [NotificationChannel.InApp], ["display_name"]),
    ];

    private static NotificationTypeDefinition Define(
        string code,
        IReadOnlyList<NotificationChannel> channels,
        string[]? variables = null,
        string[]? secretVariables = null) =>
        new(
            code,
            NotificationCategory.System,
            NotificationPriority.Normal,
            channels,
            [],
            new HashSet<string>(variables ?? [], StringComparer.Ordinal),
            new HashSet<string>(secretVariables ?? [], StringComparer.Ordinal));
}
