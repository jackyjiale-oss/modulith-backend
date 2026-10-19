using NSubstitute;
using TemplateName.Modules.Notifications.Application.Preferences.Get;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications.Preferences;

public sealed class GetPreferencesQueryHandlerTests
{
    private readonly PreferenceHandlerHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_catalog_type_is_listed_in_code_order_whatever_the_registration_order()
    {
        var result = await _harness.CreateGetHandler().HandleAsync(new GetPreferencesQuery(), Ct);

        var codes = result.Value.Types.Select(type => type.TypeCode).ToList();
        codes.Count.ShouldBe(_harness.Catalog.All.Count);
        codes.ShouldBe([.. _harness.Catalog.All.Select(type => type.Code).Order(StringComparer.Ordinal)]);
        codes.First().ShouldStartWith("auth.");
    }

    [Fact]
    public async Task Without_stored_rows_every_channel_is_enabled_and_mandatory_follows_the_type()
    {
        var result = await _harness.CreateGetHandler().HandleAsync(new GetPreferencesQuery(), Ct);

        var passwordChanged = result.Value.Types.Single(type => type.TypeCode == PreferenceHandlerHarness.PasswordChanged);
        passwordChanged.Category.ShouldBe(NotificationCategory.Security);
        passwordChanged.Channels.ShouldBe(
        [
            new ChannelPreferenceResponse(NotificationChannel.Email, Enabled: true, Mandatory: true),
            new ChannelPreferenceResponse(NotificationChannel.InApp, Enabled: true, Mandatory: false),
        ]);

        var welcome = result.Value.Types.Single(type => type.TypeCode == PreferenceHandlerHarness.Welcome);
        welcome.Category.ShouldBe(NotificationCategory.System);
        welcome.Channels.ShouldAllBe(channel => channel.Enabled && !channel.Mandatory);
        result.Value.Types.SelectMany(type => type.Channels).ShouldAllBe(channel => channel.Enabled);
    }

    [Fact]
    public async Task A_stored_disabled_row_shows_as_disabled_for_its_channel_only()
    {
        _harness.Stored.Add(_harness.Disabled(PreferenceHandlerHarness.Welcome, NotificationChannel.Email));

        var result = await _harness.CreateGetHandler().HandleAsync(new GetPreferencesQuery(), Ct);

        var welcome = result.Value.Types.Single(type => type.TypeCode == PreferenceHandlerHarness.Welcome);
        welcome.Channels.Single(channel => channel.Channel == NotificationChannel.Email).Enabled.ShouldBeFalse();
        welcome.Channels.Single(channel => channel.Channel == NotificationChannel.InApp).Enabled.ShouldBeTrue();
        result.Value.Types.SelectMany(type => type.Channels).Count(channel => !channel.Enabled).ShouldBe(1);
    }

    [Fact]
    public async Task Rows_for_a_type_or_channel_the_catalog_no_longer_has_are_ignored()
    {
        _harness.Stored.Add(_harness.Disabled("gone.type", NotificationChannel.Email));
        _harness.Stored.Add(_harness.Disabled("test.english_only", NotificationChannel.Email));

        var result = await _harness.CreateGetHandler().HandleAsync(new GetPreferencesQuery(), Ct);

        result.Value.Types.ShouldNotContain(type => type.TypeCode == "gone.type");
        result.Value.Types.SelectMany(type => type.Channels).ShouldAllBe(channel => channel.Enabled);
        result.Value.Types.Single(type => type.TypeCode == "test.english_only").Channels.ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.InApp);
    }

    [Fact]
    public async Task A_caller_without_a_user_id_reads_nothing_of_anyone()
    {
        _harness.CurrentUser.UserId.Returns((Guid?)null);
        _harness.Stored.Add(_harness.Disabled(PreferenceHandlerHarness.Welcome, NotificationChannel.Email));

        var result = await _harness.CreateGetHandler().HandleAsync(new GetPreferencesQuery(), Ct);

        // The answer is the defaults: nobody's stored rows are read for the empty id.
        result.Value.Types.SelectMany(type => type.Channels).ShouldAllBe(channel => channel.Enabled);
    }
}
