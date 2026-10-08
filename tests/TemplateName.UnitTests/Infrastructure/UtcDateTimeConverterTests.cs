using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class UtcDateTimeConverterTests
{
    private static readonly UtcDateTimeConverter Converter = new();

    [Fact]
    public void Local_value_is_converted_to_utc_on_write()
    {
        var local = new DateTime(2026, 1, 1, 8, 30, 0, DateTimeKind.Local);

        var stored = Converter.ConvertToProviderTyped(local);

        stored.ShouldBe(local.ToUniversalTime());
        stored.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Unspecified_value_is_taken_as_utc_on_write()
    {
        var unspecified = new DateTime(2026, 1, 1, 8, 30, 0, 123, DateTimeKind.Unspecified);

        var stored = Converter.ConvertToProviderTyped(unspecified);

        stored.Ticks.ShouldBe(unspecified.Ticks);
        stored.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Stored_value_is_read_back_as_utc()
    {
        var stored = new DateTime(2026, 1, 1, 8, 30, 0, 123, DateTimeKind.Unspecified);

        var read = Converter.ConvertFromProviderTyped(stored);

        read.Ticks.ShouldBe(stored.Ticks);
        read.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
