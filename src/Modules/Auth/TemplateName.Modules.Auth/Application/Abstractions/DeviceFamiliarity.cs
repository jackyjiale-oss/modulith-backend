namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>How a device (name and user agent) compares with the sessions a user has stored, in any state (D15).</summary>
internal enum DeviceFamiliarity
{
    /// <summary>
    /// The user has no stored session. That is the account's first sign-in, never a new device, only when the user never signed in
    /// before (<c>LastLoginAt</c> empty); the cleanup also leaves a dormant account without sessions, and its next sign-in is a new
    /// device. <c>SessionStarter</c> tells the two apart.
    /// </summary>
    NoStoredSession = 0,

    /// <summary>A stored session has this device name and user agent.</summary>
    Known = 1,

    /// <summary>The user has sessions, none from this device.</summary>
    New = 2,
}
