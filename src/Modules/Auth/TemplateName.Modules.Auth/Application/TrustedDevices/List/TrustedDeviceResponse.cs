namespace TemplateName.Modules.Auth.Application.TrustedDevices.List;

/// <summary>One device of the caller that can still skip the challenge. Never the token, its hash or the security stamp.</summary>
/// <param name="Id">The device id, the id <c>DELETE auth/trusted-devices/{id}</c> takes.</param>
/// <param name="DeviceName">The device name of the sign-in that remembered it, <c>Unknown</c> when the client gave none.</param>
/// <param name="UserAgent">The <c>User-Agent</c> of the verify that remembered it, if sent.</param>
/// <param name="CreatedAt">When it was remembered (UTC).</param>
/// <param name="ExpiresAt">When it must verify again (UTC, absolute: using the device never extends it).</param>
/// <param name="LastUsedAt">When it last skipped the challenge (UTC); null until then.</param>
internal sealed record TrustedDeviceResponse(
    Guid Id,
    string DeviceName,
    string? UserAgent,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastUsedAt);
