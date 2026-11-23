using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary><c>notify.PushDevices</c> and <see cref="IPushDeviceRepository"/> against the real SQL Server.</summary>
public sealed class PushDevicePersistenceTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task A_device_round_trips()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var device = PushDevice.Register(userId, sessionId, PushPlatform.Ios, new string('t', PushDevice.MaxTokenLength), "iPhone 17", "2.0.1", Now);
        device.Deactivate(PushDeviceDeactivationReason.RejectedByProvider, Now.AddMinutes(5));
        await SaveAsync(device);

        await using var scope = Factory.Services.CreateAsyncScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IPushDeviceRepository>().GetAsync(device.Id, Ct);

        loaded.ShouldNotBeNull();
        loaded.UserId.ShouldBe(userId);
        loaded.SessionId.ShouldBe(sessionId);
        loaded.Platform.ShouldBe(PushPlatform.Ios);
        loaded.Token.ShouldBe(device.Token);
        loaded.TokenHash.ShouldBe(device.TokenHash);
        loaded.DeviceName.ShouldBe("iPhone 17");
        loaded.AppVersion.ShouldBe("2.0.1");
        loaded.CreatedAt.ShouldBe(Now);
        loaded.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        loaded.LastRegisteredAt.ShouldBe(Now);
        loaded.DeactivatedAt.ShouldBe(Now.AddMinutes(5));
        loaded.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.RejectedByProvider);
    }

    [Fact]
    public async Task The_token_hash_is_unique()
    {
        await SaveAsync(PushDevice.Register(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Android, "same-token", null, null, Now));

        await Should.ThrowAsync<DbUpdateException>(
            () => SaveAsync(PushDevice.Register(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Android, "same-token", null, null, Now)));
    }

    [Fact]
    public async Task FindByTokenHash_finds_the_device_whoever_owns_it()
    {
        var device = PushDevice.Register(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Web, "web-token", null, null, Now);
        await SaveAsync(device);

        await using var scope = Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPushDeviceRepository>();

        (await repository.FindByTokenHashAsync(PushDevice.HashOf("web-token"), Ct)).ShouldNotBeNull().Id.ShouldBe(device.Id);
        (await repository.FindByTokenHashAsync(PushDevice.HashOf("other-token"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ListActive_skips_deactivated_devices_newest_first()
    {
        var userId = Guid.NewGuid();
        var oldest = PushDevice.Register(userId, Guid.NewGuid(), PushPlatform.Android, "a", null, null, Now.AddHours(-3));
        var newest = PushDevice.Register(userId, Guid.NewGuid(), PushPlatform.Android, "b", null, null, Now.AddHours(-1));
        var gone = PushDevice.Register(userId, Guid.NewGuid(), PushPlatform.Android, "c", null, null, Now.AddHours(-2));
        gone.Deactivate(PushDeviceDeactivationReason.RemovedByUser, Now);
        var someoneElses = PushDevice.Register(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Android, "d", null, null, Now);
        await SaveAsync(oldest, newest, gone, someoneElses);

        await using var scope = Factory.Services.CreateAsyncScope();
        var active = await scope.ServiceProvider.GetRequiredService<IPushDeviceRepository>().ListActiveAsync(userId, Ct);

        active.Select(device => device.Id).ShouldBe([newest.Id, oldest.Id]);
    }

    [Fact]
    public async Task Changes_to_a_loaded_device_save_through_the_unit_of_work()
    {
        var device = PushDevice.Register(Guid.NewGuid(), Guid.NewGuid(), PushPlatform.Android, "tracked", null, null, Now);
        await SaveAsync(device);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<IPushDeviceRepository>().GetAsync(device.Id, Ct);
            loaded.ShouldNotBeNull().Deactivate(PushDeviceDeactivationReason.SessionEnded, Now);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        (await QueryAsync(context => context.Set<PushDevice>().SingleAsync(row => row.Id == device.Id, Ct)))
            .DeactivationReason.ShouldBe(PushDeviceDeactivationReason.SessionEnded);
    }

    private async Task SaveAsync(params PushDevice[] devices)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        context.AddRange(devices);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }
}
