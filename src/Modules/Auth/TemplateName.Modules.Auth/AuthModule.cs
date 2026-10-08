using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Localization;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Endpoints;
using TemplateName.Modules.Auth.Infrastructure.Authorization;
using TemplateName.Modules.Auth.Infrastructure.Email;
using TemplateName.Modules.Auth.Infrastructure.Observability;
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
    /// (<c>AuthErrorMessages</c>), the SMTP email sender with <c>Auth:Links</c> and <c>Auth:Verification</c>, the sign-in settings
    /// <c>Auth:RefreshToken</c> and <c>Auth:Lockout</c>, the counters of the <c>TemplateName.Auth</c> meter (added to OpenTelemetry), the
    /// access tokens with the JWT bearer handler as the default authentication scheme, the permission checker with its cache, the
    /// permission source and the seeder, and the Data Protection key ring stored in <c>auth.DataProtectionKeys</c>. Call it after
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

        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<LinksOptions>()
            .Bind(configuration.GetSection(LinksOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddOptions<VerificationOptions>()
            .Bind(configuration.GetSection(VerificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<RefreshTokenOptions>()
            .Bind(configuration.GetSection(RefreshTokenOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<LockoutOptions>()
            .Bind(configuration.GetSection(LockoutOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The counters; the meter is added to OpenTelemetry, which exports it whenever the host exports metrics (OTLP configured).
        services.AddSingleton<IAuthMetrics, AuthMetrics>();
        services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(AuthMetrics.MeterName));

        AddAccessTokens(services, configuration);
        AddPermissions(services, configuration);

        // One key ring for every instance, so an outbox event protected by one instance can be read by another (ADR 0017).
        var applicationName = configuration["Auth:DataProtection:ApplicationName"] is { Length: > 0 } configuredName
            ? configuredName
            : DefaultDataProtectionApplicationName;
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<AuthDbContext>();

        return services;
    }

    /// <summary>
    /// Seeds the module in one transaction: the system roles (<c>SuperAdmin</c>, <c>Admin</c>, <c>User</c>); the permissions every
    /// registered <c>IPermissionSource</c> declares (new ones inserted, names and descriptions updated, undeclared ones deprecated, never
    /// deleted; an invalid definition fails the run); every non-deprecated permission for <c>SuperAdmin</c> on each run; the default
    /// permissions for <c>Admin</c> only when the role is created; and, when <c>Auth:Seed:AdminEmail</c> and <c>Auth:Seed:AdminPassword</c>
    /// are both set and no user has that email, a confirmed <c>SuperAdmin</c> user. Idempotent. The host calls it after the migration
    /// step when <c>Auth:Seed:RunOnStartup</c> is on; the test harness calls it after each database reset.
    /// </summary>
    /// <exception cref="InvalidOperationException">A permission definition or an <c>Auth:Seed</c> setting is invalid.</exception>
    public static async Task SeedAuthModuleAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthSeeder>().SeedAsync(cancellationToken);
    }

    /// <summary>
    /// Maps the module's endpoints; <paramref name="app"/> is the host's <c>/api/v1</c> group. The self-service routes live in its
    /// <c>auth</c> group: <c>POST auth/register</c>, <c>POST auth/email/confirm</c>, <c>POST auth/email/resend-confirmation</c>,
    /// <c>POST auth/login</c>, <c>POST auth/token/refresh</c>, <c>GET auth/me</c>, <c>PUT auth/me</c>, <c>GET auth/sessions</c>, <c>DELETE auth/sessions/{id}</c>, <c>POST auth/logout</c>, <c>POST auth/logout-all</c>,
    /// <c>POST auth/password/forgot</c>, <c>POST auth/password/reset</c> and <c>POST auth/password/change</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        return app.MapSelfServiceEndpoints();
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

    /// <summary>
    /// Registers the module's permission source (Ruling R2), the permission checker, reader and cache (one singleton for
    /// <c>IPermissionChecker</c>, <c>IPermissionReader</c> and <c>IPermissionCache</c>) on an in-memory <see cref="HybridCache"/> whose
    /// clock is the application's <see cref="TimeProvider"/>, and the seeder with <c>Auth:Seed</c>.
    /// </summary>
    internal static void AddPermissions(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionSource, AuthPermissionSource>());

        // In memory only until Plan 5 adds a distributed cache. The local entries live in the shared IMemoryCache, which expires them
        // by its own clock; give it the application's, so cache lifetimes follow TimeProvider like everything else (and tests can move it).
        services.AddHybridCache();
        services.AddOptions<MemoryCacheOptions>()
            .Configure<TimeProvider>((options, timeProvider) => options.Clock = new TimeProviderCacheClock(timeProvider));
        services.AddSingleton<PermissionChecker>();
        services.AddSingleton<IPermissionChecker>(serviceProvider => serviceProvider.GetRequiredService<PermissionChecker>());
        services.AddSingleton<IPermissionCache>(serviceProvider => serviceProvider.GetRequiredService<PermissionChecker>());
        services.AddSingleton<IPermissionReader>(serviceProvider => serviceProvider.GetRequiredService<PermissionChecker>());

        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName));
        services.AddScoped<PermissionSynchronizer>();
        services.AddScoped<AuthSeeder>();
    }
}
