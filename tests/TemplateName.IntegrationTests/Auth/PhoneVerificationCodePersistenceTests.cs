using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The phone number columns and <c>auth.PhoneVerificationCodes</c>: the round trip, the unique verified number (among users that are
/// not deleted), and the two conditional updates that decide a race in the database.
/// </summary>
public sealed class PhoneVerificationCodePersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const int SqlUniqueIndexViolation = 2601;
    private const string Number = "+60123456789";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly List<AsyncServiceScope> _scopes = [];

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task A_code_round_trips()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, Number, "042137", Lifetime, Now);
        await SaveAsync(user, code);

        var saved = (await Codes().GetPendingAsync(user.Id, Now, Ct)).ShouldNotBeNull();

        saved.Id.ShouldBe(code.Id);
        saved.UserId.ShouldBe(user.Id);
        saved.PhoneNumber.ShouldBe(Number);
        saved.Salt.ShouldBe(code.Salt);
        saved.CodeHash.ShouldBe(code.CodeHash);
        saved.CreatedAt.ShouldBe(Now);
        saved.ExpiresAt.ShouldBe(Now + Lifetime);
        saved.AttemptCount.ShouldBe(0);
        saved.ConsumedAt.ShouldBeNull();
        saved.InvalidatedAt.ShouldBeNull();
        saved.Matches("042137").ShouldBeTrue();
    }

    [Fact]
    public async Task The_lookups_find_the_latest_pending_code_and_count_the_codes_issued()
    {
        var user = NewUser("alice@example.com");
        var older = PhoneVerificationCode.Issue(user.Id, Number, "111111", Lifetime, Now.AddMinutes(-3));
        var newer = PhoneVerificationCode.Issue(user.Id, Number, "222222", Lifetime, Now.AddMinutes(-1));
        var expired = PhoneVerificationCode.Issue(user.Id, Number, "333333", Lifetime, Now.AddHours(-2));
        await SaveAsync(user, older, newer, expired);
        var codes = Codes();

        (await codes.GetPendingAsync(user.Id, Now, Ct)).ShouldNotBeNull().Id.ShouldBe(newer.Id);
        (await codes.ListPendingAsync(user.Id, Now, Ct)).Select(code => code.Id).ShouldBe([older.Id, newer.Id], ignoreOrder: true);
        (await codes.GetLastIssuedAtAsync(user.Id, Ct)).ShouldBe(Now.AddMinutes(-1));
        (await codes.CountIssuedSinceAsync(user.Id, Now.AddHours(-1), Ct)).ShouldBe(2);
        (await codes.CountIssuedSinceAsync(user.Id, Now.AddDays(-1), Ct)).ShouldBe(3);
        (await codes.GetLastIssuedAtAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_verified_number_is_unique()
    {
        var alice = NewUser("alice@example.com");
        alice.VerifyPhoneNumber(Number, Now);
        await SaveAsync(alice);
        var bob = NewUser("bob@example.com");
        bob.VerifyPhoneNumber(Number, Now);

        var failure = await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(bob));

        var sql = failure.InnerException.ShouldBeOfType<SqlException>();
        sql.Number.ShouldBe(SqlUniqueIndexViolation);
        sql.Message.ShouldContain(UniqueIndexNames.UserPhoneNumber);
    }

    [Fact]
    public async Task Two_users_without_a_number_are_fine()
    {
        await SaveAsync(NewUser("alice@example.com"), NewUser("bob@example.com"));

        (await QueryAsync(context => context.Set<User>().CountAsync(user => user.PhoneNumber == null, Ct))).ShouldBe(2);
    }

    [Fact]
    public async Task A_deleted_user_does_not_hold_the_number()
    {
        var alice = NewUser("alice@example.com");
        alice.VerifyPhoneNumber(Number, Now);
        await SaveAsync(alice);
        await QueryAsync(context => context.Set<User>().IgnoreQueryFilters().Where(user => user.Id == alice.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.IsDeleted, true), Ct));
        var bob = NewUser("bob@example.com");
        bob.VerifyPhoneNumber(Number, Now);

        await SaveAsync(bob);
    }

    [Fact]
    public async Task Parallel_wrong_attempts_never_exceed_the_maximum()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, Number, "042137", Lifetime, Now);
        await SaveAsync(user, code);

        var counted = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Codes().TryCountWrongAttemptAsync(code.Id, maxAttempts: 5, Now, Ct)));

        counted.Count(wasCounted => wasCounted).ShouldBe(5);
        var stored = await QueryAsync(context => context.Set<PhoneVerificationCode>().SingleAsync(row => row.Id == code.Id, Ct));
        stored.AttemptCount.ShouldBe(5);
        stored.InvalidatedAt.ShouldBe(Now);
        (await Codes().GetPendingAsync(user.Id, Now, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_wrong_attempt_below_the_maximum_keeps_the_code_pending()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, Number, "042137", Lifetime, Now);
        await SaveAsync(user, code);

        (await Codes().TryCountWrongAttemptAsync(code.Id, maxAttempts: 5, Now, Ct)).ShouldBeTrue();

        var pending = (await Codes().GetPendingAsync(user.Id, Now, Ct)).ShouldNotBeNull();
        pending.AttemptCount.ShouldBe(1);
        pending.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryConsume_wins_once()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, Number, "042137", Lifetime, Now);
        await SaveAsync(user, code);

        var claims = await Task.WhenAll(
            Codes().TryConsumeAsync(code.Id, Now, Ct),
            Codes().TryConsumeAsync(code.Id, Now, Ct));

        claims.Count(claimed => claimed).ShouldBe(1);
        (await Codes().TryCountWrongAttemptAsync(code.Id, maxAttempts: 5, Now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_expired_or_invalidated_code_cannot_be_consumed()
    {
        var user = NewUser("alice@example.com");
        var expired = PhoneVerificationCode.Issue(user.Id, Number, "042137", Lifetime, Now.AddHours(-1));
        var invalidated = PhoneVerificationCode.Issue(user.Id, Number, "042138", Lifetime, Now);
        invalidated.Invalidate(Now);
        await SaveAsync(user, expired, invalidated);

        (await Codes().TryConsumeAsync(expired.Id, Now, Ct)).ShouldBeFalse();
        (await Codes().TryConsumeAsync(invalidated.Id, Now, Ct)).ShouldBeFalse();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private User NewUser(string email) => User.Register(email, "Test user", "en", "hash-1", Now).Value;

    private IPhoneVerificationCodeRepository Codes() => NewScope().GetRequiredService<IPhoneVerificationCodeRepository>();

    private IServiceProvider NewScope()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider;
    }

    private async Task SaveAsync(params object[] entities)
    {
        var context = NewScope().GetRequiredService<AuthDbContext>();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }

    private Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query) => query(NewScope().GetRequiredService<AuthDbContext>());
}
