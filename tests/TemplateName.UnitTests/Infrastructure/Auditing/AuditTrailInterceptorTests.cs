using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Infrastructure.Common.Auditing;

namespace TemplateName.UnitTests.Infrastructure.Auditing;

/// <summary>
/// The interceptor against <see cref="AuditTestDbContext"/>, which maps no outbox. A <see cref="SuppressSaveInterceptor"/> after it keeps
/// a save that gets past it from reaching the database. The writing of the outbox row is covered by the persistence tests.
/// </summary>
public sealed class AuditTrailInterceptorTests
{
    private static readonly Guid ItemId = Guid.Parse("0a7c1b52-3d4e-4f60-8a91-b2c3d4e5f607");

    private readonly AuditTrailInterceptor _interceptor = new(
        Substitute.For<ICurrentUser>(),
        new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void SaveChanges_with_an_audited_change_and_no_outbox_mapping_throws_naming_the_context()
    {
        using var context = new AuditTestDbContext(_interceptor, new SuppressSaveInterceptor());
        context.Items.Add(NewItem());

        var exception = Should.Throw<InvalidOperationException>(() => context.SaveChanges());

        exception.Message.ShouldContain(nameof(AuditTestDbContext));
        exception.Message.ShouldContain("ApplyOutbox");
    }

    [Fact]
    public async Task SaveChangesAsync_with_an_audited_change_and_no_outbox_mapping_throws_naming_the_context()
    {
        await using var context = new AuditTestDbContext(_interceptor, new SuppressSaveInterceptor());
        context.Items.Add(NewItem());

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(nameof(AuditTestDbContext));
        exception.Message.ShouldContain("ApplyOutbox");
    }

    [Fact]
    public void Save_that_changes_only_an_ignored_property_does_not_need_an_outbox()
    {
        using var context = new AuditTestDbContext(_interceptor, new SuppressSaveInterceptor());
        var item = NewItem();
        context.Attach(item);
        item.Ignored = "changed";

        Should.NotThrow(() => context.SaveChanges());
    }

    private static AuditedItem NewItem() => new()
    {
        Id = ItemId,
        Title = "First",
        Count = 3,
        Due = new DateOnly(2026, 10, 28),
    };

    /// <summary>Ends every save before it reaches the database, as if it had written nothing.</summary>
    private sealed class SuppressSaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
            => InterceptionResult<int>.SuppressWithResult(0);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
    }
}
