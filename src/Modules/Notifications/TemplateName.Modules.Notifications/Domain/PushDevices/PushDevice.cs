using System.Security.Cryptography;
using System.Text;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Domain.PushDevices;

/// <summary>
/// One FCM registration token of a user's app, bound to the sign-in session that registered it: pushes go to it only while that
/// session is active. The token is the key the provider sends to, so it is stored, but it is a bearer value: never logged, returned by
/// an endpoint or printed (<see cref="ToString"/> leaves it out). <see cref="TokenHash"/> (SHA-256 of its UTF-8 bytes) is the unique key
/// a registration is matched by, because the token itself is too long to index.
/// </summary>
internal sealed class PushDevice
{
    public const int MaxTokenLength = 4096;

    public const int MaxDeviceNameLength = 100;

    public const int MaxAppVersionLength = 50;

    private PushDevice()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The owner; changes when another user registers the same token (a phone that changed hands).</summary>
    public Guid UserId { get; private set; }

    /// <summary>The session (the access token's <c>sid</c>) of the latest registration.</summary>
    public Guid SessionId { get; private set; }

    public PushPlatform Platform { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public string Token { get; private set; } = string.Empty;

    public string? DeviceName { get; private set; }

    public string? AppVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastRegisteredAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public PushDeviceDeactivationReason? DeactivationReason { get; private set; }

    public bool IsActive => DeactivatedAt is null;

    public static PushDevice Register(
        Guid userId,
        Guid sessionId,
        PushPlatform platform,
        string token,
        string? deviceName,
        string? appVersion,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new PushDevice
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            SessionId = sessionId,
            Platform = platform,
            TokenHash = HashOf(token),
            Token = token,
            DeviceName = BoundedText.Cut(deviceName, MaxDeviceNameLength),
            AppVersion = BoundedText.Cut(appVersion, MaxAppVersionLength),
            CreatedAt = now,
            LastRegisteredAt = now,
        };
    }

    /// <summary>The SHA-256 of <paramref name="token"/>'s UTF-8 bytes, the key a registration is looked up by.</summary>
    public static byte[] HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>A registration of the same token again: it moves to <paramref name="userId"/> and the current session and is active.</summary>
    public void Reregister(Guid userId, Guid sessionId, PushPlatform platform, string? deviceName, string? appVersion, DateTimeOffset now)
    {
        UserId = userId;
        SessionId = sessionId;
        Platform = platform;
        DeviceName = BoundedText.Cut(deviceName, MaxDeviceNameLength);
        AppVersion = BoundedText.Cut(appVersion, MaxAppVersionLength);
        LastRegisteredAt = now;
        DeactivatedAt = null;
        DeactivationReason = null;
    }

    /// <summary>Stops pushes to this device. Idempotent: a device already inactive keeps its first time and reason.</summary>
    public void Deactivate(PushDeviceDeactivationReason reason, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        DeactivatedAt = now;
        DeactivationReason = reason;
    }

    public override string ToString() => $"PushDevice {{ Id = {Id}, UserId = {UserId}, Platform = {Platform}, IsActive = {IsActive} }}";
}
