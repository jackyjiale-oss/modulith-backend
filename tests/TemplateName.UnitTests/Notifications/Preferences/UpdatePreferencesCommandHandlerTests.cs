using NSubstitute;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Application.Preferences.Update;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications.Preferences;

public sealed class UpdatePreferencesCommandHandlerTests
{
    private const string PasswordChanged = PreferenceHandlerHarness.PasswordChanged;
    private const string Welcome = PreferenceHandlerHarness.Welcome;

    private readonly PreferenceHandlerHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disabling_an_optional_channel_stores_a_disabled_row()
    {
        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, false));

        result.IsSuccess.ShouldBeTrue();
        _harness.Preferences.Received(1).Add(Arg.Is<UserPreference>(preference =>
            preference.UserId == _harness.UserId
            && preference.TypeCode == Welcome
            && preference.Channel == NotificationChannel.Email
            && !preference.IsEnabled
            && preference.UpdatedAt == PreferenceHandlerHarness.Now));
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessConflictAsync(Ct);
    }

    [Fact]
    public async Task Enabling_a_switched_off_channel_removes_its_row_because_rows_exist_only_when_they_differ_from_the_default()
    {
        var row = _harness.Disabled(Welcome, NotificationChannel.Email);
        _harness.Stored.Add(row);

        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, true));

        result.IsSuccess.ShouldBeTrue();
        _harness.Preferences.Received(1).Remove(row);
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());
    }

    [Fact]
    public async Task Enabling_a_channel_that_has_no_row_stores_nothing()
    {
        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, true));

        result.IsSuccess.ShouldBeTrue();
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());
        _harness.Preferences.DidNotReceive().Remove(Arg.Any<UserPreference>());
    }

    [Fact]
    public async Task Disabling_a_channel_that_is_already_off_keeps_the_existing_row_and_adds_no_second_one()
    {
        var row = _harness.Disabled(Welcome, NotificationChannel.Email);
        _harness.Stored.Add(row);

        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, false));

        result.IsSuccess.ShouldBeTrue();
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());
        _harness.Preferences.DidNotReceive().Remove(Arg.Any<UserPreference>());
        row.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Enabling_a_mandatory_channel_is_a_no_op()
    {
        var result = await UpdateAsync(Item(PasswordChanged, NotificationChannel.Email, true));

        result.IsSuccess.ShouldBeTrue();
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());
        _harness.Preferences.DidNotReceive().Remove(Arg.Any<UserPreference>());
    }

    [Theory]
    [InlineData("nope.nothing", "Email", "notifications.unknown_type", "typeCode")]
    [InlineData("test.english_only", "Email", "notifications.channel_not_supported", "channel")]
    [InlineData(PasswordChanged, "Email", "notifications.channel_mandatory", "channel")]
    public async Task One_invalid_item_rejects_the_whole_request_and_nothing_is_written(
        string typeCode, string channelName, string code, string parameter)
    {
        var result = await UpdateAsync(
            Item(Welcome, NotificationChannel.Email, false),
            Item(typeCode, Enum.Parse<NotificationChannel>(channelName), false),
            Item(Welcome, NotificationChannel.InApp, false));

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(code);
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Parameters!.ShouldContainKey(parameter);
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());
        _harness.Preferences.DidNotReceive().Remove(Arg.Any<UserPreference>());
        await _harness.UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessConflictAsync(Ct);
        await _harness.UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_unknown_type_error_names_the_type_code()
    {
        var result = await UpdateAsync(Item("nope.nothing", NotificationChannel.Email, false));

        result.Error.Parameters!["typeCode"].ShouldBe("nope.nothing");
    }

    [Fact]
    public async Task A_superseded_duplicate_is_still_validated()
    {
        // The first item would be refused on its own, so the request is refused even though the last one wins.
        var result = await UpdateAsync(Item(PasswordChanged, NotificationChannel.Email, false), Item(PasswordChanged, NotificationChannel.Email, true));

        result.Error.Code.ShouldBe("notifications.channel_mandatory");
    }

    [Fact]
    public async Task The_last_of_two_items_for_the_same_type_and_channel_wins()
    {
        await UpdateAsync(Item(Welcome, NotificationChannel.Email, false), Item(Welcome, NotificationChannel.Email, true));
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());

        _harness.Preferences.ClearReceivedCalls();
        await UpdateAsync(Item(Welcome, NotificationChannel.Email, true), Item(Welcome, NotificationChannel.Email, false));

        _harness.Preferences.Received(1).Add(Arg.Is<UserPreference>(preference => !preference.IsEnabled));
    }

    [Fact]
    public async Task A_conflicting_save_is_retried_against_fresh_rows()
    {
        _harness.UnitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(false, true);

        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, false));

        result.IsSuccess.ShouldBeTrue();

        // Two reads for the two attempts and one for the answer.
        await _harness.Preferences.Received(3).ListAsync(_harness.UserId, Ct);
        await _harness.UnitOfWork.Received(2).SaveChangesUnlessConflictAsync(Ct);
        await _harness.UnitOfWork.DidNotReceive().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task Conflicts_that_persist_end_in_a_plain_save_which_throws()
    {
        _harness.UnitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(false);

        await UpdateAsync(Item(Welcome, NotificationChannel.Email, false));

        // Two tolerant saves, then the plain save, which throws on a conflict that remains (409 concurrency.conflict at the HTTP boundary).
        await _harness.UnitOfWork.Received(2).SaveChangesUnlessConflictAsync(Ct);
        await _harness.UnitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_answer_is_the_updated_preferences()
    {
        _harness.Preferences.When(repository => repository.Add(Arg.Any<UserPreference>()))
            .Do(call => _harness.Stored.Add(call.Arg<UserPreference>()));

        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, false));

        result.Value.Types.Single(type => type.TypeCode == Welcome).Channels
            .Single(channel => channel.Channel == NotificationChannel.Email).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_caller_without_a_user_id_changes_nothing()
    {
        _harness.CurrentUser.UserId.Returns((Guid?)null);

        var result = await UpdateAsync(Item(Welcome, NotificationChannel.Email, false));

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Unauthorized);
        _harness.Preferences.DidNotReceive().Add(Arg.Any<UserPreference>());
        await _harness.UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessConflictAsync(Ct);
    }

    private static ChannelPreferenceUpdate Item(string typeCode, NotificationChannel channel, bool enabled) => new(typeCode, channel, enabled);

    private Task<Result<PreferencesResponse>> UpdateAsync(params ChannelPreferenceUpdate[] items)
        => _harness.CreateUpdateHandler().HandleAsync(new UpdatePreferencesCommand(items), Ct);
}
