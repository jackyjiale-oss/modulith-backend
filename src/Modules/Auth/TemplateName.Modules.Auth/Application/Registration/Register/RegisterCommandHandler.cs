using System.Globalization;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Application.Registration.Register;

/// <summary>
/// Registers an account without revealing whether the address is taken: the caller gets the same answer either way, and the password
/// is hashed before the address is looked up, so both paths pay the hashing cost. A new account gets the <c>User</c> role and an
/// email-confirmation code; an existing one only records the attempt, whose event emails its owner.
/// </summary>
internal sealed class RegisterCommandHandler(
    IUserRepository users,
    IRoleRepository roles,
    IVerificationCodeRepository verificationCodes,
    IPasswordHasher passwordHasher,
    IBreachedPasswordChecker breachedPasswordChecker,
    ISecureTokenService tokenService,
    ISecretProtector secretProtector,
    IAuthAuditWriter auditWriter,
    IClientContext clientContext,
    IUnitOfWork unitOfWork,
    IAuthMetrics metrics,
    IOptions<VerificationOptions> verificationOptions,
    TimeProvider timeProvider) : ICommandHandler<RegisterCommand>
{
    public async Task<Result> HandleAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        // Says nothing about accounts: the answer depends on the password alone.
        if (await breachedPasswordChecker.IsBreachedAsync(command.Password, cancellationToken))
        {
            return Result.Failure(UserErrors.PasswordBreached);
        }

        // Before the lookup, so a known address costs the same time as a new one.
        var passwordHash = passwordHasher.Hash(command.Password);
        var now = timeProvider.GetUtcNow();

        var existing = await users.GetByNormalizedEmailAsync(User.NormalizeEmail(command.Email), cancellationToken);
        if (existing is not null)
        {
            existing.NoteRegistrationAttempt();
            auditWriter.Record(AuthAuditLog.Create(
                AuthAuditEvents.RegisterDuplicate,
                succeeded: false,
                now,
                userId: existing.Id,
                attemptedIdentifier: AuthAuditLog.MaskIdentifier(command.Email)));
            await unitOfWork.SaveChangesAsync(cancellationToken);
            metrics.RecordRegistration();

            return Result.Success();
        }

        var userRole = await roles.GetByNormalizedNameAsync(Role.NormalizeName(SystemRoles.User), cancellationToken)
            ?? throw new InvalidOperationException(
                $"The system role '{SystemRoles.User}' does not exist, so no account can be registered. Seed the Auth module (SeedAuthModuleAsync) first.");

        // The validator accepted it as a predefined culture; store its canonical spelling (zh-Hans, not ZH-hans).
        var locale = CultureInfo.GetCultureInfo(command.Locale, predefinedOnly: true).Name;
        var registration = User.Register(command.Email, command.DisplayName.Trim(), locale, passwordHash, now);
        if (registration.IsFailure)
        {
            return Result.Failure(registration.Error);
        }

        var user = registration.Value;
        user.AssignRole(userRole.Id, assignedBy: null, now);
        users.Add(user);

        var token = tokenService.Generate();
        verificationCodes.Add(VerificationCode.Issue(
            user.Id,
            VerificationPurpose.EmailVerify,
            user.NormalizedEmail,
            token.Hash,
            secretProtector.Protect(token.Value),
            verificationOptions.Value.EmailLifetime,
            clientContext.IpAddress,
            now));

        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.Registered, succeeded: true, now, userId: user.Id));

        // One save: the user, its role, the code, the audit entry and the outbox rows of both events commit together.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // The same count for a new and an existing address: the metric tells them apart no more than the response does.
        metrics.RecordRegistration();

        return Result.Success();
    }
}
