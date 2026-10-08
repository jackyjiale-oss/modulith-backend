using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Endpoints;
using TemplateName.Modules.Auth.Infrastructure.Email;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Auth.Infrastructure.Security;
using TemplateName.Modules.Auth.Infrastructure.Tokens;
using TemplateName.Modules.Auth.Resources;

namespace TemplateName.Modules.Auth;

/// <summary>The Auth module's entry point: the host registers its services and maps its endpoints.</summary>
public static class AuthModule
{
    /// <summary>The Data Protection application name when <c>Auth:DataProtection:ApplicationName</c> is not set.</summary>
    private const string DefaultDataProtectionApplicationName = "TemplateName";

    /// <summary>
    /// Registers the module's context (schema <c>auth</c>), outbox, handlers and validators, repositories, audit writer, error messages
    /// (<c>AuthErrorMessages</c>), the SMTP email sender, the access tokens with the JWT bearer handler as the default authentication scheme,
    /// and the Data Protection key ring stored in <c>auth.DataProtectionKeys</c>. Call it after <c>AddInfrastructureCommon</c> and before
    /// <c>AddApplicationDecorators</c>.
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

        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<LinksOptions>()
            .Bind(configuration.GetSection(LinksOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        AddAccessTokens(services, configuration);

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

    /// <summary>Maps the module's endpoints that live at fixed root paths, outside <c>/api/v1</c>: <c>GET /.well-known/jwks.json</c>.</summary>
    public static IEndpointRouteBuilder MapAuthWellKnownEndpoints(this IEndpointRouteBuilder app)
    {
        return app.MapJwksEndpoints();
    }

    /// <summary>
    /// Registers <c>Auth:Jwt</c> (validated on start, including every key and, outside Development and Testing, that a key exists), the
    /// signing keys, the access token issuer and the JWT bearer handler as the default scheme (Ruling R4), whose parameters come from
    /// <see cref="JwtValidation"/> with the application's <see cref="TimeProvider"/> and <c>MapInboundClaims = false</c>.
    /// </summary>
    internal static void AddAccessTokens(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate<IHostEnvironment>(
                (options, environment) => options.SigningKeys.Count > 0 || SigningKeyProvider.AllowsEphemeralKey(environment),
                SigningKeyProvider.MissingKeysMessage)
            .ValidateOnStart();
        services.AddSingleton<SigningKeyProvider>();
        services.AddSingleton<ISigningKeyProvider>(serviceProvider => serviceProvider.GetRequiredService<SigningKeyProvider>());
        // Built when the host starts (after the options are validated), so the keys load and an ephemeral key is logged at start.
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<SigningKeyProvider>());
        services.AddSingleton<IAccessTokenIssuer, AccessTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, ISigningKeyProvider, TimeProvider>((bearer, jwt, keys, timeProvider) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = JwtValidation.CreateParameters(jwt.Value, keys, timeProvider);
            });
    }
}
