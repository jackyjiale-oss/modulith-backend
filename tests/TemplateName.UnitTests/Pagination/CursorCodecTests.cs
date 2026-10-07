using System.Buffers.Text;
using System.Text;
using TemplateName.Application.Common.Pagination;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Pagination;

public sealed class CursorCodecTests
{
    private static readonly SortField CreatedAt = new("createdAt", "[CreatedAt]", typeof(DateTime));
    private static readonly SortField StartDate = new("startDate", "[StartDate]", typeof(DateOnly));
    private static readonly SortField IdField = new("id", "[Id]", typeof(Guid));
    private static readonly SortField[] Allowed = [CreatedAt, StartDate];

    private static readonly SortSpecification NewestFirst = SortSpecification.Parse("-createdAt", Allowed, IdField, "-createdAt").Value;
    private static readonly SortSpecification ByStartDate = SortSpecification.Parse("startDate", Allowed, IdField, "-createdAt").Value;
    private static readonly string FilterHash = CursorCodec.ComputeFilterHash("employeeId=;status=");

    [Fact]
    public void Round_trips_utc_datetime_with_millisecond_precision_and_guid()
    {
        var createdAt = new DateTime(2026, 1, 1, 8, 30, 0, 123, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        var token = CursorCodec.Encode(new Cursor(CursorDirection.Previous, NewestFirst.Signature, FilterHash, [createdAt, id]));

        var cursor = CursorCodec.Decode(token, NewestFirst, FilterHash).Value;

        cursor.Direction.ShouldBe(CursorDirection.Previous);
        cursor.SortSignature.ShouldBe("-createdAt,-id");
        cursor.FilterHash.ShouldBe(FilterHash);
        cursor.KeyValues.Count.ShouldBe(2);
        var decodedCreatedAt = cursor.KeyValues[0].ShouldBeOfType<DateTime>();
        decodedCreatedAt.ShouldBe(createdAt);
        decodedCreatedAt.Ticks.ShouldBe(createdAt.Ticks);
        decodedCreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        cursor.KeyValues[1].ShouldBe(id);
    }

    [Fact]
    public void Round_trips_date_only()
    {
        var id = Guid.NewGuid();
        var token = CursorCodec.Encode(new Cursor(CursorDirection.Next, ByStartDate.Signature, FilterHash, [new DateOnly(2026, 2, 28), id]));

        var cursor = CursorCodec.Decode(token, ByStartDate, FilterHash).Value;

        cursor.KeyValues.ShouldBe([new DateOnly(2026, 2, 28), id]);
    }

    [Fact]
    public void Token_is_url_safe()
    {
        var token = CursorCodec.Encode(new Cursor(CursorDirection.Next, NewestFirst.Signature, FilterHash, [new DateTime(2026, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc), Guid.NewGuid()]));

        token.ShouldAllBe(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_');
    }

    [Fact]
    public void Garbage_token_is_invalid()
    {
        Decode("abc").Error.Code.ShouldBe("pagination.invalid_cursor");
        Decode(ToBase64Url("{}")).Error.Code.ShouldBe("pagination.invalid_cursor");
        Decode(new string('a', 2000)).Error.Code.ShouldBe("pagination.invalid_cursor");
        Decode(string.Empty).Error.Code.ShouldBe("pagination.invalid_cursor");
        Decode("!!!not base64!!!").Error.Code.ShouldBe("pagination.invalid_cursor");
        Decode(ToBase64Url("null")).Error.Code.ShouldBe("pagination.invalid_cursor");
        Decode(ToBase64Url("[1,2]")).Error.Code.ShouldBe("pagination.invalid_cursor");
    }

    [Fact]
    public void Token_longer_than_1024_characters_is_invalid_even_when_well_formed()
    {
        var oversized = ToBase64Url(Payload(version: 1, keys: $"\"2026-01-01T00:00:00.0000000Z\",\"{Guid.Empty}\"", padding: new string(' ', 1000)));

        oversized.Length.ShouldBeGreaterThan(1024);
        Decode(oversized).Error.Code.ShouldBe("pagination.invalid_cursor");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Wrong_version_is_invalid(int version)
        => Decode(ToBase64Url(Payload(version, $"\"2026-01-01T00:00:00.0000000Z\",\"{Guid.Empty}\""))).Error.Code.ShouldBe("pagination.invalid_cursor");

    [Fact]
    public void Unknown_direction_is_invalid()
        => Decode(ToBase64Url(Payload(1, $"\"2026-01-01T00:00:00.0000000Z\",\"{Guid.Empty}\"", direction: 3))).Error.Code.ShouldBe("pagination.invalid_cursor");

    [Theory]
    [InlineData("\"not a date\",\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"2026-01-01T00:00:00.0000000Z\",\"not a guid\"")]
    [InlineData("\"2026-01-01T00:00:00.0000000+08:00\",\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"2026-01-01T00:00:00.0000000\",\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"2026-01-01T00:00:00.0000000Z\"")]
    [InlineData("\"2026-01-01T00:00:00.0000000Z\",\"00000000-0000-0000-0000-000000000000\",\"extra\"")]
    [InlineData("1,2")]
    public void Key_values_that_do_not_match_the_sort_fields_are_invalid(string keys)
        => Decode(ToBase64Url(Payload(1, keys))).Error.Code.ShouldBe("pagination.invalid_cursor");

    [Fact]
    public void Different_sort_signature_is_a_mismatch()
    {
        var token = CursorCodec.Encode(new Cursor(CursorDirection.Next, NewestFirst.Signature, FilterHash, [DateTime.UnixEpoch, Guid.NewGuid()]));

        CursorCodec.Decode(token, ByStartDate, FilterHash).Error.Code.ShouldBe("pagination.cursor_mismatch");
    }

    [Fact]
    public void Different_filter_hash_is_a_mismatch()
    {
        var token = CursorCodec.Encode(new Cursor(CursorDirection.Next, NewestFirst.Signature, FilterHash, [DateTime.UnixEpoch, Guid.NewGuid()]));

        CursorCodec.Decode(token, NewestFirst, CursorCodec.ComputeFilterHash("employeeId=1;status=")).Error.Code.ShouldBe("pagination.cursor_mismatch");
    }

    [Fact]
    public void Filter_hash_is_the_first_16_bytes_of_sha256_in_hex()
    {
        // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
        CursorCodec.ComputeFilterHash("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223");
    }

    [Fact]
    public void Encoding_a_non_utc_datetime_throws()
        => Should.Throw<ArgumentException>(() => CursorCodec.Encode(
            new Cursor(CursorDirection.Next, NewestFirst.Signature, FilterHash, [new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), Guid.NewGuid()])));

    private static Result<Cursor> Decode(string token) => CursorCodec.Decode(token, NewestFirst, FilterHash);

    private static string Payload(int version, string keys, int direction = 1, string padding = "")
        => $"{{{padding}\"v\":{version},\"d\":{direction},\"s\":\"-createdAt,-id\",\"f\":\"{FilterHash}\",\"k\":[{keys}]}}";

    private static string ToBase64Url(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
