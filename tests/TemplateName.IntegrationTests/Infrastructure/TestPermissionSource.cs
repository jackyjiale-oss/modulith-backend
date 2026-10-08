using TemplateName.Application.Common.Identity;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// A permission source a test can change between seeding runs, as if a module had declared or dropped a permission. It declares
/// nothing until <see cref="Declare"/> is called, and every test starts with it empty.
/// </summary>
public sealed class TestPermissionSource : IPermissionSource
{
    private PermissionDefinition[] _permissions = [];

    public IReadOnlyCollection<PermissionDefinition> Permissions => Volatile.Read(ref _permissions);

    public void Declare(params PermissionDefinition[] permissions) => Volatile.Write(ref _permissions, permissions);

    public void Reset() => Declare();
}
