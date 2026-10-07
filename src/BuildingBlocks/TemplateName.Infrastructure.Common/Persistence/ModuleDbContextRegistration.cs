namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Marks a module context for <see cref="MigrationExtensions.MigrateModuleDatabasesAsync"/>; one is registered per context, in order.</summary>
/// <param name="ContextType">The <c>DbContext</c> type to migrate.</param>
internal sealed record ModuleDbContextRegistration(Type ContextType);
