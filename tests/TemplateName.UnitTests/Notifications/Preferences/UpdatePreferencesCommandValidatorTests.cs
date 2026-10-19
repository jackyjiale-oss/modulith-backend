using TemplateName.Modules.Notifications.Application.Preferences.Update;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.UnitTests.Notifications.Preferences;

public sealed class UpdatePreferencesCommandValidatorTests
{
    private readonly UpdatePreferencesCommandValidator _sut = new();

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public void Item_count_must_be_between_1_and_100(int count, bool valid)
    {
        var result = _sut.Validate(new UpdatePreferencesCommand(Items(count)));

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Items");
        }
    }

    [Fact]
    public void A_missing_list_is_invalid_not_a_crash()
    {
        var result = _sut.Validate(new UpdatePreferencesCommand(null!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Items");
    }

    [Fact]
    public void A_null_item_is_invalid_not_a_crash()
    {
        var result = _sut.Validate(new UpdatePreferencesCommand([new("auth.password_changed", NotificationChannel.InApp, false), null!]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Items");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_type_code_is_invalid(string typeCode)
    {
        var result = _sut.Validate(new UpdatePreferencesCommand([new(typeCode, NotificationChannel.InApp, false)]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Items[0].TypeCode");
    }

    [Fact]
    public void A_type_code_longer_than_the_column_is_invalid()
    {
        var result = _sut.Validate(new UpdatePreferencesCommand([new(new string('a', 101), NotificationChannel.InApp, false)]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Items[0].TypeCode");
    }

    [Fact]
    public void A_channel_that_is_not_defined_is_invalid()
    {
        var result = _sut.Validate(new UpdatePreferencesCommand([new("auth.password_changed", (NotificationChannel)99, false)]));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Items[0].Channel");
    }

    private static List<ChannelPreferenceUpdate> Items(int count)
        => [.. Enumerable.Range(0, count).Select(index => new ChannelPreferenceUpdate($"test.type_{index}", NotificationChannel.InApp, Enabled: false))];
}
