using System.Globalization;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
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
/// email-confirmation code; an existing one only records the attempt, whose event emails its owner. Two registrations of one new address
/// at the same moment both pass the lookup; the unique email index lets one insert win and the other is answered as for a known address.
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

        var normalizedEmail = User.NormalizeEmail(command.Email);
        var existing = await users.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken);
        if (existing is not null)
        {
            return await RecordAttemptOnKnownAddressAsync(existing, command.Email, now, cancellationToken);
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
            VerificationTrigger.SelfService,
            user.NormalizedEmail,
            token.Hash,
            secretProtector.Protect(token.Value),
            verificationOptions.Value.EmailLifetime,
            clientContext.IpAddress,
            now));

        auditWriter.Record(AuthAuditLog.Create(AuthAuditEvents.Registered, succeeded: true, now, userId: user.Id));

        // One save: the user, its role, the code, the audit entry and the outbox rows of both events commit together. A refusal by
        // the email index means a simultaneous registration created the address after the lookup above: nothing of ours was saved,
        // and the request is answered exactly as for a known address (enumeration rule), never with a 500.
        if (!await unitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.UserEmail, cancellationToken)
            && await users.GetByNormalizedEmailAsync(normalizedEmail, cancellationToken) is { } winner)
        {
            return await RecordAttemptOnKnownAddressAsync(winner, command.Email, now, cancellationToken);
        }

        // The same count for a new and an existing address: the metric tells them apart no more than the response does.
        metrics.RecordRegistration();

        return Result.Success();
    }

    // The existing account is left as it is; only the attempt is noted, so its owner gets a notice and the caller the usual answer.
    private async Task<Result> RecordAttemptOnKnownAddressAsync(User existing, string attemptedEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        existing.NoteRegistrationAttempt();
        auditWriter.Record(AuthAuditLog.Create(
            AuthAuditEvents.RegisterDuplicate,
            succeeded: false,
            now,
            userId: existing.Id,
            attemptedIdentifier: AuthAuditLog.MaskIdentifier(attemptedEmail)));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        metrics.RecordRegistration();

        return Result.Success();
    }
}
