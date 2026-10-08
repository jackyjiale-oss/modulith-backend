using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Admin.Users;
using TemplateName.Modules.Auth.Application.Passwords;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.UnitTests.Auth.Admin;

/// <summary>
/// The doubles the admin user handlers share: repositories, the permission cache, the audit writer and the unit of work as NSubstitute
/// fakes, a seeded <c>SuperAdmin</c> role, the real <see cref="SuperAdminRules"/> and <see cref="PasswordResetLinkIssuer"/> over them,
/// and an acting administrator who is not a SuperAdmin unless a test says so.
/// </summary>
public abstract class AdminHandlerTestBase
{
    internal const string TokenValue = "admin-issued-reset-token";

    internal static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    internal static readonly byte[] TokenHash = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    private protected AdminHandlerTestBase()
    {
        ActorId = Guid.NewGuid();
        SuperAdminRole = Role.CreateSystem(SystemRoles.SuperAdmin, "Every permission.", Now.AddDays(-30));
        UserRole = Role.CreateSystem(SystemRoles.User, "Signed-in user.", Now.AddDays(-30));
        Roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.SuperAdmin), Arg.Any<CancellationToken>()).Returns(SuperAdminRole);
        Roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.User), Arg.Any<CancellationToken>()).Returns(UserRole);
        Roles.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<Role>)[.. KnownRoles.Where(role => call.Arg<IReadOnlyCollection<Guid>>().Contains(role.Id))]);
        KnownRoles.AddRange([SuperAdminRole, UserRole]);

        TokenService.Generate().Returns(new GeneratedToken(TokenValue, TokenHash));
        SecretProtector.Protect(Arg.Any<string>()).Returns(call => "protected:" + call.Arg<string>());
        ClientContext.IpAddress.Returns("203.0.113.7");
        AuditWriter.Record(Arg.Do<AuthAuditLog>(AuditEntries.Add));
        VerificationCodes.Add(Arg.Do<VerificationCode>(IssuedCodes.Add));

        Rules = new SuperAdminRules(Roles, Users);
        LinkIssuer = new PasswordResetLinkIssuer(
            VerificationCodes,
            TokenService,
            SecretProtector,
            ClientContext,
            Options.Create(new VerificationOptions()));
    }

    internal Guid ActorId { get; }

    internal Role SuperAdminRole { get; }

    internal Role UserRole { get; }

    internal List<Role> KnownRoles { get; } = [];

    internal IUserRepository Users { get; } = Substitute.For<IUserRepository>();

    internal IRoleRepository Roles { get; } = Substitute.For<IRoleRepository>();

    internal ISessionRepository Sessions { get; } = Substitute.For<ISessionRepository>();

    internal IVerificationCodeRepository VerificationCodes { get; } = Substitute.For<IVerificationCodeRepository>();

    internal ISecureTokenService TokenService { get; } = Substitute.For<ISecureTokenService>();

    internal ISecretProtector SecretProtector { get; } = Substitute.For<ISecretProtector>();

    internal IClientContext ClientContext { get; } = Substitute.For<IClientContext>();

    internal IPermissionCache PermissionCache { get; } = Substitute.For<IPermissionCache>();

    internal IAuthAuditWriter AuditWriter { get; } = Substitute.For<IAuthAuditWriter>();

    internal IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();

    internal FakeTimeProvider Time { get; } = new(Now);

    internal SuperAdminRules Rules { get; }

    internal PasswordResetLinkIssuer LinkIssuer { get; }

    internal List<AuthAuditLog> AuditEntries { get; } = [];

    internal List<VerificationCode> IssuedCodes { get; } = [];

    private protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Makes the acting administrator an active SuperAdmin (or not) as the repository reports it.</summary>
    internal void GivenActorIsSuperAdmin(bool isSuperAdmin = true)
        => Users.IsActiveInRoleAsync(ActorId, SuperAdminRole.Id, Arg.Any<CancellationToken>()).Returns(isSuperAdmin);

    /// <summary>A confirmed, active user with a password and the given roles, found by id through the repository.</summary>
    internal User GivenUser(params Role[] roles)
    {
        var user = User.Register($"user-{Guid.NewGuid():N}@example.com", "Target", "en", "hash", Now.AddDays(-10)).Value;
        user.ConfirmEmail(Now.AddDays(-10));
        foreach (var role in roles)
        {
            user.AssignRole(role.Id, assignedBy: null, Now.AddDays(-10));
        }

        Users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    /// <summary>A custom role the repository knows by id.</summary>
    internal Role GivenRole(string name)
    {
        var role = Role.Create(name, $"The {name} role.", Now.AddDays(-5)).Value;
        KnownRoles.Add(role);
        return role;
    }

    /// <summary>Active sessions of <paramref name="user"/> as <see cref="ISessionRepository.GetActiveByUserAsync"/> returns them.</summary>
    internal List<UserSession> GivenActiveSessions(User user, int count)
    {
        var sessions = Enumerable.Range(0, count)
            .Select(index => UserSession.Start(
                user.Id,
                "pwd",
                $"Device {index}",
                userAgent: null,
                ipAddress: null,
                user.SecurityStamp,
                [.. Enumerable.Repeat((byte)index, 32)],
                TimeSpan.FromDays(14),
                TimeSpan.FromDays(90),
                Now.AddHours(-1)).Session)
            .ToList();
        Sessions.GetActiveByUserAsync(user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(sessions);
        return sessions;
    }

    /// <summary>The <c>actorId</c> member of an audit entry's <c>Details</c>, which must be valid JSON.</summary>
    internal static Guid ActorOf(AuthAuditLog entry)
    {
        using var details = JsonDocument.Parse(entry.Details!);
        return details.RootElement.GetProperty("actorId").GetGuid();
    }
}
