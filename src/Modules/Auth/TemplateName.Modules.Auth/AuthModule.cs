using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Infrastructure.Security;
using TemplateName.Modules.Auth.Resources;

namespace TemplateName.Modules.Auth;

/// <summary>The Auth module's entry point: the host registers its services and maps its endpoints.</summary>
public static class AuthModule
{
    /// <summary>The Data Protection application name when <c>Auth:DataProtection:ApplicationName</c> is not set.</summary>
    private const string DefaultDataProtectionApplicationName = "TemplateName";

    /// <summary>
    /// Registers the module's context (schema <c>auth</c>), outbox, handlers and validators, repositories, audit writer, error messages
    /// (<c>AuthErrorMessages</c>) and the Data Protection key ring stored in <c>auth.DataProtectionKeys</c>. Call it after
    /// <c>AddInfrastructureCommon</c> and before <c>AddApplicationDecorators</c>.
    /// </summary>
    public static IServiceCollection AddAuthModule(this IServiceCollection services, IConfiguration configuration)
    {
        var assembly = typeof(AuthModule).Assembly;

        services.AddModuleDbContext<AuthDbContext>(AuthDbContext.Schema);
        services.AddOutbox<AuthDbContext>(assembly);
        services.AddApplicationHandlers(assembly);
        services.AddErrorMessages<AuthErrorMessages>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IVerificationCodeRepository, VerificationCodeRepository>();
        services.AddScoped<IAuthAuditWriter, AuthAuditWriter>();
        services.AddScoped<IUnitOfWork>(serviceProvider => serviceProvider.GetRequiredService<AuthDbContext>());

        services.AddHttpContextAccessor();
        services.AddScoped<IClientContext, HttpClientContext>();

        services.AddOptions<PasswordOptions>()
            .Bind(configuration.GetSection(PasswordOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ISecureTokenService, SecureTokenService>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddHttpClient(HibpBreachedPasswordChecker.HttpClientName, client => client.Timeout = HibpBreachedPasswordChecker.RequestBudget);
        services.AddSingleton<IBreachedPasswordChecker, HibpBreachedPasswordChecker>();

        // One key ring for every instance, so an outbox event protected by one instance can be read by another (ADR 0017).
        var applicationName = configuration["Auth:DataProtection:ApplicationName"] is { Length: > 0 } configuredName
            ? configuredName
            : DefaultDataProtectionApplicationName;
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<AuthDbContext>();

        return services;
    }

    /// <summary>Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group. Empty until the first endpoint lands.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        return app;
    }
}
