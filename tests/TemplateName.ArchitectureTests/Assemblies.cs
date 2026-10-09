using System.Reflection;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Resources;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Auth.Resources;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Resources;
using TemplateName.Modules.Sample;
using TemplateName.Modules.Sample.Resources;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Resources;
using TemplateName.Web.Common.Results;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// The assemblies the architecture rules inspect. Adding a module means adding one line to <see cref="Modules"/> (and one to <see cref="Contracts"/> when it has a contracts project) and one to
/// <see cref="ErrorMessageResources"/>.
/// </summary>
public static class Assemblies
{
    public static Assembly SharedKernel { get; } = typeof(IDomainEvent).Assembly;

    public static Assembly ApplicationCommon { get; } = typeof(IBaseCommand).Assembly;

    public static Assembly InfrastructureCommon { get; } = typeof(OutboxOptions).Assembly;

    public static Assembly WebCommon { get; } = typeof(ResultExtensions).Assembly;

    public static Assembly Api { get; } = typeof(Program).Assembly;

    /// <summary>One entry per business module; each is the module's own assembly (never a <c>.Contracts</c> assembly).</summary>
    public static IReadOnlyList<Assembly> Modules { get; } =
    [
        typeof(AuthModule).Assembly,
        typeof(NotificationsModule).Assembly,
        typeof(SampleModule).Assembly,
    ];

    /// <summary>One entry per <c>*.Contracts</c> assembly: the public surface a module offers the others (see <c>ContractsTests</c>).</summary>
    public static IReadOnlyList<Assembly> Contracts { get; } =
    [
        typeof(IUserContactDirectory).Assembly,
    ];

    /// <summary>The building-block assemblies (everything under <c>src/BuildingBlocks</c>).</summary>
    public static IReadOnlyList<Assembly> BuildingBlocks { get; } =
    [
        SharedKernel,
        ApplicationCommon,
        InfrastructureCommon,
        WebCommon,
    ];

    /// <summary>The marker class of every <c>*ErrorMessages</c> resource set (neutral English plus one <c>.resx</c> per translation).</summary>
    public static IReadOnlyList<Type> ErrorMessageResources { get; } =
    [
        typeof(CommonErrorMessages),
        typeof(InfrastructureErrorMessages),
        typeof(AuthErrorMessages),
        typeof(NotificationsErrorMessages),
        typeof(SampleErrorMessages),
    ];

    /// <summary>Every <c>TemplateName.*</c> assembly under <c>src</c>.</summary>
    public static IReadOnlyList<Assembly> All { get; } =
    [
        .. BuildingBlocks,
        .. Modules,
        Api,
    ];

    /// <summary>The open generic interfaces a command, query, domain event or integration event handler implements.</summary>
    public static IReadOnlyList<Type> HandlerInterfaces { get; } =
    [
        typeof(ICommandHandler<>),
        typeof(ICommandHandler<,>),
        typeof(IQueryHandler<,>),
        typeof(IDomainEventHandler<>),
        typeof(IIntegrationEventHandler<>),
    ];

    /// <summary>Theory data: the assembly name of each module, which doubles as its root namespace.</summary>
    public static TheoryData<string> ModuleNames => new(Modules.Select(module => module.GetName().Name!));

    public static Assembly Module(string name) => Modules.Single(module => module.GetName().Name == name);
}
