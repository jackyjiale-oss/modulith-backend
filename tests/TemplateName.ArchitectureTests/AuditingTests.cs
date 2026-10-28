using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TemplateName.Modules.Audit;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// The rules of the audit trail (Plan 5b): which entities are audited, what their EF mapping must allow (an application-generated key,
/// no owned or complex property), that no secret-looking property reaches the trail unmarked, where the audit types live, and the Audit
/// module's boundaries. The mapping rules read the model of every module context, built without a database.
/// </summary>
public sealed class AuditingTests
{
    private static readonly string[] SecretNameSuffixes = ["Hash", "Secret", "Token", "Salt", "Key", "Stamp"];

    private static readonly Type[] AuditTypes =
    [
        typeof(AuditedAttribute),
        typeof(AuditIgnoreAttribute),
        typeof(AuditMaskAttribute),
        typeof(EntityChange),
        typeof(EntityChangeAction),
        typeof(EntityChangesRecordedDomainEvent),
    ];

    private static readonly Lazy<IReadOnlyList<IModel>> ModuleModels = new(BuildModuleModels);

    /// <summary>Theory data: property names the secret-name rule flags, and names it lets through.</summary>
    public static TheoryData<string, bool> SecretNames => new()
    {
        { "PasswordHash", true },
        { "SecurityStamp", true },
        { "Password", true },
        { "ClientSecret", true },
        { "RefreshToken", true },
        { "Salt", true },
        { "ApiKey", true },
        { "PasswordChangedAt", false },
        { "Email", false },
        { "PermissionId", false },
        { "RowVersion", false },
    };

    [Fact]
    public void The_audited_entities_are_the_five_of_the_first_set()
    {
        AuditedTypes().Select(type => type.FullName).ShouldBe(
            [
                "TemplateName.Modules.Sample.Domain.LeaveRequests.LeaveRequest",
                "TemplateName.Modules.Auth.Domain.Users.User",
                "TemplateName.Modules.Auth.Domain.Roles.Role",
                "TemplateName.Modules.Auth.Domain.Roles.RolePermission",
                "TemplateName.Modules.Auth.Domain.Users.UserRole",
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Audited_entities_ignore_exactly_the_derived_high_frequency_and_secret_properties_and_mask_nothing()
    {
        var ignored = AuditedTypes().ToDictionary(type => type.Name, type => MarkedPropertyNames(type, typeof(AuditIgnoreAttribute)));

        ignored["User"].ShouldBe(["NormalizedEmail", "LastLoginAt", "AccessFailedCount", "PasswordHash", "SecurityStamp"], ignoreOrder: true);
        ignored["Role"].ShouldBe(["NormalizedName"]);
        ignored["LeaveRequest"].ShouldBeEmpty();
        ignored["RolePermission"].ShouldBeEmpty();
        ignored["UserRole"].ShouldBeEmpty();
        AuditedTypes().SelectMany(type => MarkedPropertyNames(type, typeof(AuditMaskAttribute))).ShouldBeEmpty();
    }

    [Fact]
    public void Every_audited_entity_is_mapped_by_a_module_context()
    {
        var mapped = AuditedEntityTypes().Select(entityType => entityType.ClrType).Distinct().ToList();

        mapped.ShouldNotBeEmpty();
        mapped.ShouldBe(AuditedTypes(), ignoreOrder: true);
    }

    [Fact]
    public void Audited_entities_have_application_generated_keys()
    {
        // The trail names an added entity by its key, so the key must be known before the save; composite keys included.
        var entityTypes = AuditedEntityTypes();
        entityTypes.ShouldNotBeEmpty();

        var storeGenerated = entityTypes
            .SelectMany(entityType => entityType.FindPrimaryKey()!.Properties
                .Where(property => property.ValueGenerated != ValueGenerated.Never)
                .Select(property => $"{entityType.ClrType.Name}.{property.Name} ({property.ValueGenerated})"))
            .ToList();

        storeGenerated.ShouldBeEmpty(string.Join(", ", storeGenerated));
    }

    [Fact]
    public void Audited_entities_have_no_owned_or_complex_property()
    {
        // The extractor records scalar properties only; a value inside an owned or complex type would be left out of the trail.
        var entityTypes = AuditedEntityTypes();
        entityTypes.ShouldNotBeEmpty();

        var nested = entityTypes
            .SelectMany(entityType => entityType.GetComplexProperties().Select(property => $"{entityType.ClrType.Name}.{property.Name} (complex)")
                .Concat(entityType.GetNavigations()
                    .Where(navigation => navigation.TargetEntityType.IsOwned())
                    .Select(navigation => $"{entityType.ClrType.Name}.{navigation.Name} (owned)")))
            .ToList();

        nested.ShouldBeEmpty(string.Join(", ", nested));
        entityTypes.ShouldAllBe(entityType => !entityType.IsOwned());
    }

    [Fact]
    public void A_secret_looking_property_of_an_audited_entity_is_ignored_or_masked()
    {
        var entityTypes = AuditedEntityTypes();
        entityTypes.ShouldNotBeEmpty();

        var unmarked = entityTypes
            .SelectMany(entityType => entityType.GetProperties()
                .Where(property => IsSecretName(property.Name) && !IsIgnoredOrMasked(property.PropertyInfo))
                .Select(property => $"{entityType.ClrType.Name}.{property.Name}"))
            .ToList();

        unmarked.ShouldBeEmpty(string.Join(", ", unmarked));
    }

    [Theory]
    [MemberData(nameof(SecretNames))]
    public void The_secret_name_rule_flags_names_ending_in_a_secret_word_or_equal_to_Password(string propertyName, bool flagged)
        => IsSecretName(propertyName).ShouldBe(flagged);

    [Fact]
    public void The_audit_attributes_and_change_types_are_declared_in_the_shared_kernel()
    {
        AuditTypes.ShouldAllBe(type => type.Assembly == Assemblies.SharedKernel && type.Namespace == "TemplateName.SharedKernel.Auditing");

        // No second declaration elsewhere that an entity or a module could pick up by mistake.
        var names = AuditTypes.Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        var duplicates = Assemblies.All.Concat(Assemblies.Contracts)
            .Where(assembly => assembly != Assemblies.SharedKernel)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => names.Contains(type.Name))
            .Select(type => type.FullName)
            .ToList();

        duplicates.ShouldBeEmpty(string.Join(", ", duplicates));
    }

    [Fact]
    public void The_Audit_module_references_no_other_module_and_no_module_references_it()
    {
        var audit = typeof(AuditModule).Assembly;
        var auditName = audit.GetName().Name!;
        Assemblies.Modules.ShouldContain(audit);

        // Not even a contracts assembly: Audit consumes a building-block event and declares its permission through IPermissionSource.
        audit.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("TemplateName.Modules.", StringComparison.Ordinal))
            .ShouldBeEmpty();

        Assemblies.Modules.Concat(Assemblies.Contracts)
            .Where(assembly => assembly != audit)
            .Where(assembly => assembly.GetReferencedAssemblies().Any(reference => reference.Name == auditName))
            .Select(assembly => assembly.GetName().Name)
            .ShouldBeEmpty();

        // The project files too: a reference nothing uses is dropped from the compiled assembly but still couples the projects.
        var modulesDirectory = Path.Combine(RepositoryPaths.Root, "src", "Modules");
        Directory.GetFiles(modulesDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(project => Path.GetFileNameWithoutExtension(project) != auditName)
            .Where(project => File.ReadAllText(project).Contains($"{auditName}.csproj", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ShouldBeEmpty();
        File.ReadAllText(Path.Combine(modulesDirectory, "Audit", auditName, $"{auditName}.csproj"))
            .ShouldNotContain("TemplateName.Modules.");
    }

    private static bool IsSecretName(string propertyName)
        => propertyName == "Password" || SecretNameSuffixes.Any(suffix => propertyName.EndsWith(suffix, StringComparison.Ordinal));

    private static bool IsIgnoredOrMasked(PropertyInfo? property)
        => property is not null
            && (property.IsDefined(typeof(AuditIgnoreAttribute), inherit: true) || property.IsDefined(typeof(AuditMaskAttribute), inherit: true));

    private static List<Type> AuditedTypes()
        => [.. Assemblies.All.Concat(Assemblies.Contracts)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsDefined(typeof(AuditedAttribute), inherit: false))];

    private static List<string> MarkedPropertyNames(Type type, Type attribute)
        => [.. type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.IsDefined(attribute, inherit: true))
            .Select(property => property.Name)];

    private static List<IEntityType> AuditedEntityTypes()
        => [.. ModuleModels.Value
            .SelectMany(model => model.GetEntityTypes())
            .Where(entityType => entityType.ClrType.IsDefined(typeof(AuditedAttribute), inherit: false))];

    /// <summary>The design-time model of every context a module declares, built against SQL Server without connecting to it.</summary>
    private static List<IModel> BuildModuleModels()
    {
        var contextTypes = Assemblies.Modules
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(DbContext)))
            .ToList();
        contextTypes.ShouldNotBeEmpty();

        List<IModel> models = [];
        foreach (var contextType in contextTypes)
        {
            var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
            optionsBuilder.UseSqlServer("Server=unused;Database=unused");
            using var context = (DbContext)Activator.CreateInstance(contextType, optionsBuilder.Options)!;
            models.Add(context.GetService<IDesignTimeModel>().Model);
        }

        return models;
    }
}
