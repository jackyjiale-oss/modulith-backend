using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Redis;

/// <summary>
/// The Redis tests share one Valkey container, started once. Some of them stop and restart it, so they belong to one collection and never
/// run beside each other (the assembly runs every test one at a time anyway).
/// </summary>
[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<ValkeyFixture>
{
    public const string Name = "Redis";
}
