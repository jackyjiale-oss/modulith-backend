using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Persistence;

public sealed class PersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly Guid UserA = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private readonly List<AsyncServiceScope> _scopes = [];

    [Fact]
    public async Task Added_entity_gets_created_audit_fields()
    {
        Factory.CurrentUser.UserId = UserA;
        var e = TestAggregate.Create("a", Factory.Time.GetUtcNow());
        await using (var db = NewTestDbContext()) { db.Add(e); await db.SaveChangesAsync(Ct); }
        await using var read = NewTestDbContext();
        var saved = await read.Set<TestAggregate>().SingleAsync(x => x.Id == e.Id, Ct);
        saved.CreatedAt.ShouldBe(Factory.Time.GetUtcNow().UtcDateTime);
        saved.CreatedBy.ShouldBe(UserA);
    }

    [Fact]
    public async Task Modified_entity_gets_updated_audit_fields()
    {
        Factory.CurrentUser.UserId = UserA;
        var aggregate = await SaveNewAggregateAsync("a");
        Factory.Time.Advance(TimeSpan.FromMinutes(1));

        await using (var db = NewTestDbContext())
        {
            var loaded = await db.Set<TestAggregate>().SingleAsync(x => x.Id == aggregate.Id, Ct);
            loaded.Rename("b");
            await db.SaveChangesAsync(Ct);
        }

        await using var read = NewTestDbContext();
        var saved = await read.Set<TestAggregate>().SingleAsync(x => x.Id == aggregate.Id, Ct);
        saved.Name.ShouldBe("b");
        saved.UpdatedAt.ShouldBe(Factory.Time.GetUtcNow().UtcDateTime);
        saved.UpdatedBy.ShouldBe(UserA);
    }

    [Fact]
    public async Task Deleted_entity_is_soft_deleted_and_hidden()
    {
        Factory.CurrentUser.UserId = UserA;
        var aggregate = await SaveNewAggregateAsync("a");
        Factory.Time.Advance(TimeSpan.FromMinutes(1));

        await using (var db = NewTestDbContext())
        {
            var loaded = await db.Set<TestAggregate>().SingleAsync(x => x.Id == aggregate.Id, Ct);
            db.Remove(loaded);
            await db.SaveChangesAsync(Ct);
        }

        await using var read = NewTestDbContext();
        (await read.Set<TestAggregate>().AnyAsync(x => x.Id == aggregate.Id, Ct)).ShouldBeFalse();
        var deleted = await read.Set<TestAggregate>().IgnoreQueryFilters(["SoftDelete"]).SingleAsync(x => x.Id == aggregate.Id, Ct);
        deleted.IsDeleted.ShouldBeTrue();
        deleted.DeletedAt.ShouldBe(Factory.Time.GetUtcNow().UtcDateTime);
        deleted.DeletedBy.ShouldBe(UserA);
    }

    [Fact]
    public async Task DateTime_values_round_trip_as_utc()
    {
        var aggregate = await SaveNewAggregateAsync("a");

        await using var read = NewTestDbContext();
        var saved = await read.Set<TestAggregate>().SingleAsync(x => x.Id == aggregate.Id, Ct);

        saved.CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        JsonSerializer.Serialize(saved.CreatedAt).ShouldEndWith("Z\"");
    }

    [Fact]
    public async Task Ready_includes_database_checks()
    {
        using var response = await Client.GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var registrations = Factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.ShouldContain(registration => registration.Name == TestDbContext.Schema && registration.Tags.Contains("ready"));
    }

    [Fact]
    public void Missing_connection_string_fails_startup()
    {
        using var configured = Factory.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Database", ""));

        var exception = Should.Throw<Exception>(() => configured.CreateClient());

        ExceptionChain(exception).OfType<OptionsValidationException>().ShouldNotBeEmpty();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var scope in _scopes)
        {
            await scope.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    private TestDbContext NewTestDbContext()
    {
        var scope = Factory.Services.CreateAsyncScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<TestDbContext>();
    }

    private async Task<TestAggregate> SaveNewAggregateAsync(string name)
    {
        var aggregate = TestAggregate.Create(name, Factory.Time.GetUtcNow());
        await using var db = NewTestDbContext();
        db.Add(aggregate);
        await db.SaveChangesAsync(Ct);
        return aggregate;
    }

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        var pending = new Queue<Exception>([exception]);
        while (pending.TryDequeue(out var current))
        {
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }
    }
}
