using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Contracts.Users;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;
using TemplateName.Modules.Notifications.Application.Preferences.UpdateQuietHours;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications.Preferences;

/// <summary>
/// The quiet-hours rules live in the handler, not in a validator: a one-sided or empty window must answer with the code
/// <c>notifications.invalid_quiet_hours</c>, which a FluentValidation message cannot carry.
/// </summary>
public sealed class QuietHoursHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeOnly Night = new(22, 30);
    private static readonly TimeOnly Morning = new(7, 0);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly IUserContactDirectory _contacts = Substitute.For<IUserContactDirectory>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _time = new(Now);
    private UserNotificationProfile? _profile;

    public QuietHoursHandlerTests()
    {
        _currentUser.UserId.Returns(_userId);
        _preferences.GetSettingsAsync(_userId, Arg.Any<CancellationToken>()).Returns(_ => _profile);
        _preferences.When(repository => repository.Add(Arg.Any<UserNotificationProfile>())).Do(call => _profile = call.Arg<UserNotificationProfile>());
        _unitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(true);
        Contact("Asia/Kuala_Lumpur");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setting_a_window_creates_the_profile_on_first_use_and_answers_with_the_auth_time_zone()
    {
        var result = await UpdateAsync(Night, Morning);

        result.Value.ShouldBe(new QuietHoursResponse(Night, Morning, "Asia/Kuala_Lumpur"));
        _profile.ShouldNotBeNull().QuietHours.ShouldBe(new QuietHours(Night, Morning));
        _profile.UserId.ShouldBe(_userId);
        _profile.UpdatedAt.ShouldBe(Now);
        _preferences.Received(1).Add(Arg.Any<UserNotificationProfile>());
        await _unitOfWork.Received(1).SaveChangesUnlessConflictAsync(Ct);
    }

    [Fact]
    public async Task Setting_a_window_updates_the_existing_profile()
    {
        _profile = UserNotificationProfile.Create(_userId, Now.AddDays(-1));
        _profile.SetQuietHours(new QuietHours(new TimeOnly(1, 0), new TimeOnly(2, 0)), Now.AddDays(-1));

        var result = await UpdateAsync(Night, Morning);

        result.IsSuccess.ShouldBeTrue();
        _profile.QuietHours.ShouldBe(new QuietHours(Night, Morning));
        _preferences.DidNotReceive().Add(Arg.Any<UserNotificationProfile>());
    }

    [Fact]
    public async Task Both_null_clears_an_existing_window()
    {
        _profile = UserNotificationProfile.Create(_userId, Now.AddDays(-1));
        _profile.SetQuietHours(new QuietHours(Night, Morning), Now.AddDays(-1));

        var result = await UpdateAsync(null, null);

        result.Value.ShouldBe(new QuietHoursResponse(null, null, "Asia/Kuala_Lumpur"));
        _profile.QuietHours.ShouldBeNull();
        _profile.UpdatedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Clearing_when_there_is_no_profile_creates_nothing()
    {
        var result = await UpdateAsync(null, null);

        result.Value.ShouldBe(new QuietHoursResponse(null, null, "Asia/Kuala_Lumpur"));
        _preferences.DidNotReceive().Add(Arg.Any<UserNotificationProfile>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Exactly_one_null_is_invalid_and_changes_nothing(bool hasStart, bool hasEnd)
    {
        _profile = UserNotificationProfile.Create(_userId, Now.AddDays(-1));
        _profile.SetQuietHours(new QuietHours(new TimeOnly(1, 0), new TimeOnly(2, 0)), Now.AddDays(-1));

        var result = await UpdateAsync(hasStart ? Night : null, hasEnd ? Morning : null);

        AssertInvalid(result);
        _profile.QuietHours.ShouldBe(new QuietHours(new TimeOnly(1, 0), new TimeOnly(2, 0)));
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessConflictAsync(Ct);
    }

    [Fact]
    public async Task Equal_start_and_end_are_invalid_and_create_no_profile()
    {
        var result = await UpdateAsync(Night, Night);

        AssertInvalid(result);
        _preferences.DidNotReceive().Add(Arg.Any<UserNotificationProfile>());
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessConflictAsync(Ct);
    }

    [Fact]
    public async Task A_window_that_crosses_midnight_is_valid()
    {
        (await UpdateAsync(new TimeOnly(23, 59), new TimeOnly(0, 0))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Conflicts_that_persist_end_in_a_plain_save_which_throws()
    {
        _unitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(false);

        await UpdateAsync(Night, Morning);

        await _unitOfWork.Received(2).SaveChangesUnlessConflictAsync(Ct);
        await _unitOfWork.Received(1).SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_conflicting_save_is_retried_against_fresh_rows()
    {
        _unitOfWork.SaveChangesUnlessConflictAsync(Arg.Any<CancellationToken>()).Returns(false, true);

        var result = await UpdateAsync(Night, Morning);

        result.IsSuccess.ShouldBeTrue();
        await _unitOfWork.Received(2).SaveChangesUnlessConflictAsync(Ct);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_caller_without_a_user_id_changes_nothing()
    {
        _currentUser.UserId.Returns((Guid?)null);

        var result = await UpdateAsync(Night, Morning);

        result.Error.Type.ShouldBe(ErrorType.Unauthorized);
        _preferences.DidNotReceive().Add(Arg.Any<UserNotificationProfile>());
    }

    [Fact]
    public async Task Get_without_a_profile_shows_no_window()
    {
        var result = await CreateGetHandler().HandleAsync(new GetQuietHoursQuery(), Ct);

        result.Value.ShouldBe(new QuietHoursResponse(null, null, "Asia/Kuala_Lumpur"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/AZone")]
    [InlineData("Eastern Standard Time Nope")]
    public async Task An_empty_or_unknown_auth_time_zone_is_shown_as_utc(string timeZone)
    {
        Contact(timeZone);

        var result = await CreateGetHandler().HandleAsync(new GetQuietHoursQuery(), Ct);

        result.Value.TimeZone.ShouldBe("UTC");
    }

    [Fact]
    public async Task A_user_the_directory_does_not_know_is_shown_utc()
    {
        _contacts.FindAsync(_userId, Arg.Any<CancellationToken>()).Returns((UserContact?)null);

        var result = await CreateGetHandler().HandleAsync(new GetQuietHoursQuery(), Ct);

        result.Value.TimeZone.ShouldBe("UTC");
    }

    private static void AssertInvalid(Result<QuietHoursResponse> result)
    {
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("notifications.invalid_quiet_hours");
        result.Error.Type.ShouldBe(ErrorType.Validation);
    }

    private void Contact(string timeZone)
        => _contacts.FindAsync(_userId, Arg.Any<CancellationToken>()).Returns(new UserContact(_userId, "alice@example.com", "Alice", "en", timeZone));

    private GetQuietHoursQueryHandler CreateGetHandler() => new(_preferences, _contacts, _currentUser);

    private Task<Result<QuietHoursResponse>> UpdateAsync(TimeOnly? start, TimeOnly? end)
        => new UpdateQuietHoursCommandHandler(_preferences, _currentUser, _unitOfWork, CreateGetHandler(), _time)
            .HandleAsync(new UpdateQuietHoursCommand(start, end), Ct);
}
