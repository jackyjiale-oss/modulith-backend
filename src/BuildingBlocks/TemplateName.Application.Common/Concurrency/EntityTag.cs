namespace TemplateName.Application.Common.Concurrency;

/// <summary>
/// An opaque version of a resource's editable state, sent as the <c>ETag</c> header and compared with <c>If-Match</c> (ADR 0038).
/// <see cref="Value"/> is the text between the quotes; build one with <see cref="EntityTagBuilder"/>.
/// </summary>
public readonly record struct EntityTag(string Value)
{
    /// <summary>The header form: the value in double quotes, a strong tag.</summary>
    public string ToHeaderValue() => $"\"{Value}\"";
}
