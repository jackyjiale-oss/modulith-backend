using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class UtcDateTimeOffsetConverterTests
{
    private static readonly UtcDateTimeOffsetConverter Converter = new();

    [Fact]
    public void Value_with_an_offset_is_stored_as_its_utc_instant()
    {
        var local = new DateTimeOffset(2026, 1, 1, 8, 30, 0, 123, TimeSpan.FromHours(8));

        var stored = Converter.ConvertToProviderTyped(local);

        stored.ShouldBe(new DateTime(2026, 1, 1, 0, 30, 0, 123, DateTimeKind.Utc));
        stored.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public void Stored_value_is_read_back_with_offset_zero()
    {
        var stored = new DateTime(2026, 1, 1, 0, 30, 0, 123, DateTimeKind.Unspecified);

        var read = Converter.ConvertFromProviderTyped(stored);

        read.Offset.ShouldBe(TimeSpan.Zero);
        read.UtcDateTime.Ticks.ShouldBe(stored.Ticks);
        read.UtcDateTime.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
