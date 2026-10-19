namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>How a device (name and user agent) compares with the sessions a user has stored, in any state (D15).</summary>
internal enum DeviceFamiliarity
{
    /// <summary>The user has no session yet: the account's first sign-in is never a new device.</summary>
    FirstSignIn = 0,

    /// <summary>A stored session has this device name and user agent.</summary>
    Known = 1,

    /// <summary>The user has sessions, none from this device.</summary>
    New = 2,
}
