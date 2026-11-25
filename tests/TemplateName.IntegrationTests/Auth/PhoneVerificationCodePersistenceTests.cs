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
        var code = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "042137", "protected", Lifetime, Now);
        await SaveAsync(user, code);

        var saved = (await Codes().GetPendingAsync(user.Id, PhoneCodePurpose.Verify, Now, Ct)).ShouldNotBeNull();

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
    public async Task The_lookups_find_the_latest_pending_code_and_list_the_codes_issued_oldest_first()
    {
        var user = NewUser("alice@example.com");
        var older = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "111111", "protected", Lifetime, Now.AddMinutes(-3));
        var newer = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "222222", "protected", Lifetime, Now.AddMinutes(-1));
        var expired = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "333333", "protected", Lifetime, Now.AddHours(-2));
        await SaveAsync(user, older, newer, expired);
        var codes = Codes();

        (await codes.GetPendingAsync(user.Id, PhoneCodePurpose.Verify, Now, Ct)).ShouldNotBeNull().Id.ShouldBe(newer.Id);
        (await codes.ListPendingAsync(user.Id, PhoneCodePurpose.Verify, Now, Ct)).Select(code => code.Id).ShouldBe([older.Id, newer.Id], ignoreOrder: true);
        (await codes.GetLastIssuedAtAsync(user.Id, Ct)).ShouldBe(Now.AddMinutes(-1));
        (await codes.ListIssuedSinceAsync(user.Id, Now.AddHours(-1), Ct)).ShouldBe([Now.AddMinutes(-3), Now.AddMinutes(-1)]);
        (await codes.ListIssuedSinceAsync(user.Id, Now.AddDays(-1), Ct)).ShouldBe([Now.AddHours(-2), Now.AddMinutes(-3), Now.AddMinutes(-1)]);
        (await codes.GetByIdAsync(expired.Id, Ct)).ShouldNotBeNull().Id.ShouldBe(expired.Id);
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

    [Theory]
    [InlineData((int)PhoneCodePurpose.Verify)]
    [InlineData((int)PhoneCodePurpose.SignIn)]
    public async Task Parallel_claims_never_exceed_the_maximum(int purpose)
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, (PhoneCodePurpose)purpose, Number, "042137", "protected", Lifetime, Now);
        await SaveAsync(user, code);

        var claimed = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Codes().TryClaimAttemptAsync(code.Id, maxAttempts: 5, Now, Ct)));

        claimed.Count(wasClaimed => wasClaimed).ShouldBe(5);
        (await QueryAsync(context => context.Set<PhoneVerificationCode>().SingleAsync(row => row.Id == code.Id, Ct))).AttemptCount.ShouldBe(5);
        (await Codes().TryConsumeAsync(code.Id, Now, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_claim_below_the_maximum_keeps_the_code_pending()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "042137", "protected", Lifetime, Now);
        await SaveAsync(user, code);

        (await Codes().TryClaimAttemptAsync(code.Id, maxAttempts: 5, Now, Ct)).ShouldBeTrue();

        var pending = (await Codes().GetPendingAsync(user.Id, PhoneCodePurpose.Verify, Now, Ct)).ShouldNotBeNull();
        pending.AttemptCount.ShouldBe(1);
        pending.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public async Task TryConsume_wins_once()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "042137", "protected", Lifetime, Now);
        await SaveAsync(user, code);

        var claims = await Task.WhenAll(
            Codes().TryConsumeAsync(code.Id, Now, Ct),
            Codes().TryConsumeAsync(code.Id, Now, Ct));

        claims.Count(claimed => claimed).ShouldBe(1);
        (await Codes().TryClaimAttemptAsync(code.Id, maxAttempts: 5, Now, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_expired_or_invalidated_code_cannot_be_consumed()
    {
        var user = NewUser("alice@example.com");
        var expired = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "042137", "protected", Lifetime, Now.AddHours(-1));
        var invalidated = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "042138", "protected", Lifetime, Now);
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

    [Fact]
    public async Task Purpose_round_trips()
    {
        var user = NewUser("alice@example.com");
        var code = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.SignIn, Number, "042137", "protected", Lifetime, Now);
        await SaveAsync(user, code);

        (await Codes().GetPendingAsync(user.Id, PhoneCodePurpose.SignIn, Now, Ct)).ShouldNotBeNull().Purpose.ShouldBe(PhoneCodePurpose.SignIn);
    }

    [Fact]
    public async Task Rows_written_before_the_column_read_as_verify()
    {
        var user = NewUser("alice@example.com");
        await SaveAsync(user);
        var id = Guid.CreateVersion7();
        await QueryAsync(context => context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO auth.PhoneVerificationCodes (Id, UserId, PhoneNumber, CodeHash, Salt, CreatedAt, ExpiresAt, AttemptCount)
            VALUES (@Id, @UserId, @PhoneNumber, @CodeHash, @Salt, @CreatedAt, @ExpiresAt, 0)
            """,
            [
                new SqlParameter("@Id", id),
                new SqlParameter("@UserId", user.Id),
                new SqlParameter("@PhoneNumber", Number),
                new SqlParameter("@CodeHash", new byte[32]),
                new SqlParameter("@Salt", new byte[16]),
                new SqlParameter("@CreatedAt", Now.UtcDateTime),
                new SqlParameter("@ExpiresAt", (Now + Lifetime).UtcDateTime),
            ],
            Ct));

        var saved = (await Codes().GetPendingAsync(user.Id, PhoneCodePurpose.Verify, Now, Ct)).ShouldNotBeNull();

        saved.Id.ShouldBe(id);
        saved.Purpose.ShouldBe(PhoneCodePurpose.Verify);
    }

    [Fact]
    public async Task Pending_lookup_is_per_purpose()
    {
        var user = NewUser("alice@example.com");
        var verify = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "111111", "protected", Lifetime, Now.AddSeconds(-2));
        var signIn = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.SignIn, Number, "222222", "protected", Lifetime, Now.AddSeconds(-1));
        await SaveAsync(user, verify, signIn);

        (await Codes().GetPendingAsync(user.Id, PhoneCodePurpose.Verify, Now, Ct)).ShouldNotBeNull().Id.ShouldBe(verify.Id);
        (await Codes().GetPendingAsync(user.Id, PhoneCodePurpose.SignIn, Now, Ct)).ShouldNotBeNull().Id.ShouldBe(signIn.Id);
        (await Codes().ListPendingAsync(user.Id, PhoneCodePurpose.SignIn, Now, Ct)).Select(code => code.Id).ShouldBe([signIn.Id]);
    }

    [Fact]
    public async Task Listing_with_no_purpose_returns_both()
    {
        var user = NewUser("alice@example.com");
        var verify = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "111111", "protected", Lifetime, Now);
        var signIn = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.SignIn, Number, "222222", "protected", Lifetime, Now);
        await SaveAsync(user, verify, signIn);

        (await Codes().ListPendingAsync(user.Id, null, Now, Ct)).Select(code => code.Id).ShouldBe([verify.Id, signIn.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task The_budget_counts_both_purposes()
    {
        var user = NewUser("alice@example.com");
        var verify = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.Verify, Number, "111111", "protected", Lifetime, Now.AddMinutes(-5));
        var signIn = PhoneVerificationCode.Issue(user.Id, PhoneCodePurpose.SignIn, Number, "222222", "protected", Lifetime, Now.AddMinutes(-1));
        await SaveAsync(user, verify, signIn);

        (await Codes().GetLastIssuedAtAsync(user.Id, Ct)).ShouldBe(Now.AddMinutes(-1));
        (await Codes().ListIssuedSinceAsync(user.Id, Now.AddDays(-1), Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetByPhoneNumber_finds_the_verified_number_only()
    {
        var alice = NewUser("alice@example.com");
        alice.VerifyPhoneNumber(Number, Now);
        var bob = NewUser("bob@example.com");
        bob.VerifyPhoneNumber("+60198765432", Now);
        await SaveAsync(alice, bob);
        await QueryAsync(context => context.Set<User>().IgnoreQueryFilters().Where(user => user.Id == bob.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(user => user.IsDeleted, true), Ct));

        (await Users().GetByPhoneNumberAsync(Number, Ct)).ShouldNotBeNull().Id.ShouldBe(alice.Id);
        (await Users().GetByPhoneNumberAsync("+60198765432", Ct)).ShouldBeNull();
        (await Users().GetByPhoneNumberAsync("+60111111111", Ct)).ShouldBeNull();
    }

    private IUserRepository Users() => NewScope().GetRequiredService<IUserRepository>();
}
