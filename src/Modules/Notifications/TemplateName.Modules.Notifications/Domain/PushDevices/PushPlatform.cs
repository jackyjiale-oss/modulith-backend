namespace TemplateName.Modules.Notifications.Domain.PushDevices;

/// <summary>The platform a push device runs on. The values are persisted: never renumber or reuse them.</summary>
internal enum PushPlatform : byte
{
    Android = 1,
    Ios = 2,
    Web = 3,
}
