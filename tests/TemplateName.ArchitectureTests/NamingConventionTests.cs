using System.Reflection;
using System.Runtime.CompilerServices;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using TemplateName.Application.Common.Messaging;

namespace TemplateName.ArchitectureTests;

/// <summary>Enforces docs/coding-conventions.md Sections 1 to 4.</summary>
public sealed class NamingConventionTests
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly string[] ForbiddenSuffixes =
        ["Manager", "Helper", "Helpers", "Util", "Utils", "Utility", "Impl", "Dto", "Settings", "Info"];

    [Fact]
    public void Commands_end_with_Command()
    {
        // Interfaces (ICommand, ICommand<T>) are excluded; the rule is about concrete command records.
        var commands = Types.InAssemblies(Assemblies.All).That().AreClasses().And().ImplementInterface(typeof(IBaseCommand));
        commands.GetTypes().ShouldNotBeEmpty();

        var result = commands.Should().HaveNameEndingWith("Command").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Queries_end_with_Query()
    {
        var queries = Types.InAssemblies(Assemblies.All).That().AreClasses().And().ImplementInterface(typeof(IQuery<>));
        queries.GetTypes().ShouldNotBeEmpty();

        var result = queries.Should().HaveNameEndingWith("Query").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Validators_end_with_Validator()
    {
        var validators = Types.InAssemblies(Assemblies.All).That().Inherit(typeof(AbstractValidator<>));
        validators.GetTypes().ShouldNotBeEmpty();

        var result = validators.Should().HaveNameEndingWith("Validator").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Handlers_end_with_Handler()
    {
        var failingTypeNames = new List<string>();
        var handlerCount = 0;

        foreach (var handlerInterface in Assemblies.HandlerInterfaces)
        {
            // Decorators (nested in a *Decorator class, e.g. LoggingDecorator.CommandHandler<T>) wrap handlers and are not handlers themselves.
            var handlers = Types.InAssemblies(Assemblies.All).That().AreClasses().And().AreNotNested().And().ImplementInterface(handlerInterface);
            handlerCount += handlers.GetTypes().Count();

            var result = handlers.Should().HaveNameEndingWith("Handler").GetResult();
            failingTypeNames.AddRange(result.FailingTypeNames ?? []);
        }

        handlerCount.ShouldBeGreaterThan(0);
        failingTypeNames.ShouldBeEmpty(string.Join(", ", failingTypeNames));
    }

    [Fact]
    public void Repositories_end_with_Repository()
    {
        // A repository is any class implementing an interface named I{X}Repository.
        var repositoryTypes = Assemblies.All
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsClass && type.GetInterfaces().Any(contract => contract.Name.EndsWith("Repository", StringComparison.Ordinal)))
            .ToList();
        repositoryTypes.ShouldNotBeEmpty();

        var failingTypeNames = repositoryTypes
            .Where(type => !type.Name.EndsWith("Repository", StringComparison.Ordinal))
            .Select(type => type.FullName);

        failingTypeNames.ShouldBeEmpty();
    }

    [Fact]
    public void DbContexts_end_with_DbContext()
    {
        var dbContexts = Types.InAssemblies(Assemblies.All).That().Inherit(typeof(DbContext));
        dbContexts.GetTypes().ShouldNotBeEmpty();

        var result = dbContexts.Should().HaveNameEndingWith("DbContext").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Entity_configurations_end_with_Configuration()
    {
        var configurations = Types.InAssemblies(Assemblies.All).That().AreClasses().And().ImplementInterface(typeof(IEntityTypeConfiguration<>));
        configurations.GetTypes().ShouldNotBeEmpty();

        var result = configurations.Should().HaveNameEndingWith("Configuration").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void No_type_uses_a_forbidden_suffix()
    {
        var failingTypeNames = new List<string>();

        foreach (var suffix in ForbiddenSuffixes)
        {
            var result = Types.InAssemblies(Assemblies.All).ShouldNot().HaveNameEndingWith(suffix).GetResult();
            failingTypeNames.AddRange(result.FailingTypeNames ?? []);
        }

        failingTypeNames.ShouldBeEmpty(string.Join(", ", failingTypeNames));
    }

    [Fact]
    public void Task_returning_methods_end_with_Async()
    {
        var failingMethods = DeclaredMethods()
            .OfType<MethodInfo>()
            .Where(method => IsAsynchronous(method.ReturnType))
            .Where(method => !method.Name.EndsWith("Async", StringComparison.Ordinal))
            .Where(method => !IsFixedByExternalContract(method))
            .Select(Describe);

        failingMethods.ShouldBeEmpty();
    }

    [Fact]
    public void Cancellation_token_parameter_is_last_and_named_cancellationToken()
    {
        var failingMethods = DeclaredMethods()
            .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)))
            .Where(method =>
            {
                var parameters = method.GetParameters();
                var last = parameters[^1];
                var tokenCount = parameters.Count(parameter => parameter.ParameterType == typeof(CancellationToken));
                return last.ParameterType != typeof(CancellationToken) || last.Name != "cancellationToken" || tokenCount > 1;
            })
            .Select(Describe);

        failingMethods.ShouldBeEmpty();
    }

    /// <summary>Methods and constructors declared in <c>src</c>, minus compiler-generated ones (lambdas, local functions, state machines, top-level statements).</summary>
    private static IEnumerable<MethodBase> DeclaredMethods() =>
        Assemblies.All.SelectMany(assembly => assembly.GetTypes()
            .Where(type => !IsCompilerGenerated(type))
            .SelectMany(type => type.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(type.GetConstructors(DeclaredMembers)))
            .Where(method => !IsCompilerGenerated(method))
            .Where(method => method != assembly.EntryPoint));

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.Name.Contains('<', StringComparison.Ordinal) || member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);

    private static bool IsAsynchronous(Type returnType) =>
        returnType == typeof(Task)
        || returnType == typeof(ValueTask)
        || returnType.IsSubclassOf(typeof(Task))
        || (returnType.IsGenericType
            && (returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)
                || returnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)));

    /// <summary>True for overrides and interface implementations of non-TemplateName types, whose names the framework fixes.</summary>
    private static bool IsFixedByExternalContract(MethodInfo method)
    {
        if (!IsTemplateName(method.GetBaseDefinition().DeclaringType!))
        {
            return true;
        }

        var declaringType = method.DeclaringType!;

        return !declaringType.IsInterface
            && declaringType.GetInterfaces()
                .Where(contract => !IsTemplateName(contract))
                .Any(contract => declaringType.GetInterfaceMap(contract).TargetMethods.Contains(method));
    }

    private static bool IsTemplateName(Type type) => type.Assembly.GetName().Name!.StartsWith("TemplateName", StringComparison.Ordinal);

    private static string Describe(MethodBase method) => $"{method.DeclaringType!.FullName}.{method.Name}";
}
