using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Inbox.MarkRead;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications.Inbox;

public sealed class MarkReadCommandHandlerTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IInAppNotificationRepository _inbox = Substitute.For<IInAppNotificationRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MarkReadCommandHandler _sut;

    public MarkReadCommandHandlerTests()
    {
        _currentUser.UserId.Returns(UserId);
        _sut = new MarkReadCommandHandler(_inbox, _currentUser, _unitOfWork, new FakeTimeProvider(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MarkRead_of_another_users_row_is_not_found()
    {
        // The repository only returns a row to its owner, so the caller's own id is what must reach it.
        var othersRow = GivenRow(Guid.NewGuid());

        var result = await _sut.HandleAsync(new MarkReadCommand(othersRow.Id), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("notifications.notification_not_found");
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        othersRow.ReadAt.ShouldBeNull();
        await _inbox.Received(1).GetForUserAsync(othersRow.Id, UserId, Ct);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task MarkRead_of_an_unknown_row_looks_exactly_like_another_users_row()
    {
        var othersRow = GivenRow(Guid.NewGuid());
        var unknownId = Guid.NewGuid();

        var other = await _sut.HandleAsync(new MarkReadCommand(othersRow.Id), Ct);
        var unknown = await _sut.HandleAsync(new MarkReadCommand(unknownId), Ct);

        other.Error.Code.ShouldBe(unknown.Error.Code);
        other.Error.Type.ShouldBe(unknown.Error.Type);
        other.Error.Message.ShouldBe(InAppNotificationErrors.NotFound(othersRow.Id).Message);
        unknown.Error.Message.ShouldBe(InAppNotificationErrors.NotFound(unknownId).Message);
    }

    [Fact]
    public async Task MarkRead_without_a_signed_in_user_is_not_found()
    {
        _currentUser.UserId.Returns((Guid?)null);
        var row = GivenRow(UserId);

        var result = await _sut.HandleAsync(new MarkReadCommand(row.Id), Ct);

        result.Error.Code.ShouldBe("notifications.notification_not_found");
        row.ReadAt.ShouldBeNull();
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task MarkRead_of_my_unread_row_stores_the_time_once()
    {
        var row = GivenRow(UserId);

        var result = await _sut.HandleAsync(new MarkReadCommand(row.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        row.ReadAt.ShouldBe(Now.UtcDateTime);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkRead_of_a_row_already_read_keeps_the_first_time_and_saves_nothing()
    {
        var row = GivenRow(UserId);
        row.MarkRead(Created.AddMinutes(5));

        var result = await _sut.HandleAsync(new MarkReadCommand(row.Id), Ct);

        result.IsSuccess.ShouldBeTrue();
        row.ReadAt.ShouldBe(Created.AddMinutes(5).UtcDateTime);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    // The substitute answers like the repository: the row is returned for its owner only.
    private InAppNotification GivenRow(Guid ownerId)
    {
        var row = InAppNotification.Create(
            Guid.NewGuid(), ownerId, Guid.NewGuid(), "auth.password_changed", NotificationCategory.Security, "Password changed", "Your password was changed.", Created);
        _inbox.GetForUserAsync(row.Id, ownerId, Arg.Any<CancellationToken>()).Returns(row);
        return row;
    }
}
