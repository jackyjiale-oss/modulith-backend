namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>A new bearer secret: <paramref name="Value"/> goes to its owner once, only <paramref name="Hash"/> is stored.</summary>
/// <param name="Value">The token, 43 base64url characters.</param>
/// <param name="Hash">The SHA-256 of the UTF-8 bytes of <paramref name="Value"/> (32 bytes).</param>
internal sealed record GeneratedToken(string Value, byte[] Hash);
