using System.ComponentModel.DataAnnotations;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Connection strings (section <c>ConnectionStrings</c>). Supplied by user secrets or the environment, never by appsettings files.</summary>
internal sealed class ConnectionStringsOptions
{
    internal const string SectionName = "ConnectionStrings";

    /// <summary>The application database that every module context uses unless it names another connection string.</summary>
    [Required]
    public string Database { get; set; } = string.Empty;
}
