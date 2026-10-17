using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// <see cref="IUserContactDirectory"/> as the Notifications module calls it: one Dapper query on <c>auth.Users</c>, which bypasses the
/// EF Core soft-delete filter, so these tests prove the SQL repeats it.
/// </summary>
public sealed class UserContactDirectoryTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Finds_the_contact_with_locale_and_time_zone()
    {
        var user = await CreateUserAsync("Ada@Example.com", locale: "ms");
        await SetTimeZoneAsync(user.Id, "Asia/Kuala_Lumpur");

        var contact = await FindAsync(user.Id);

        contact.ShouldNotBeNull().ShouldBe(new UserContact(user.Id, "Ada@Example.com", "Test user", "ms", "Asia/Kuala_Lumpur"));
    }

    [Fact]
    public async Task Soft_deleted_user_is_not_found()
    {
        var user = await CreateUserAsync("gone@example.com");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            context.Remove(await context.Set<User>().SingleAsync(candidate => candidate.Id == user.Id, Ct));
            await context.SaveChangesAsync(Ct);
        }

        // The row is still there, flagged; only the query's own filter hides it.
        (await QueryAsync(context => context.Set<User>().IgnoreQueryFilters().CountAsync(candidate => candidate.Id == user.Id && candidate.IsDeleted, Ct)))
            .ShouldBe(1);
        (await FindAsync(user.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_user_is_null()
    {
        await CreateUserAsync("someone@example.com");

        (await FindAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task Suspended_user_is_found()
    {
        var user = await CreateUserAsync("locked@example.com", suspended: true);

        // Security notices still reach a suspended user.
        var contact = await FindAsync(user.Id);

        contact.ShouldNotBeNull().Email.ShouldBe("locked@example.com");
    }

    [Fact]
    public async Task Locale_and_time_zone_are_returned_as_stored_so_the_consumer_applies_its_fallback()
    {
        // Both columns are not null, but nothing forces a registered locale to be non-empty; the directory never guesses a default.
        var user = await CreateUserAsync("blank@example.com", locale: string.Empty);

        var contact = await FindAsync(user.Id);

        contact.ShouldNotBeNull().Locale.ShouldBe(string.Empty);
        contact.TimeZone.ShouldBe("UTC");
    }

    private async Task<UserContact?> FindAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserContactDirectory>().FindAsync(userId, Ct);
    }

    private async Task SetTimeZoneAsync(Guid userId, string timeZone)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await context.Set<User>().SingleAsync(candidate => candidate.Id == userId, Ct);
        user.UpdateProfile(user.DisplayName, user.Locale, timeZone);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }
}
