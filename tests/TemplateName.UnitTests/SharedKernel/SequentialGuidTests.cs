using System.Data.SqlTypes;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.SharedKernel;

public sealed class SequentialGuidTests
{
    [Fact]
    public void Later_timestamp_sorts_after_earlier_in_sql_server_order()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        new SqlGuid(SequentialGuid.Create(timestamp.AddMilliseconds(1)))
            .CompareTo(new SqlGuid(SequentialGuid.Create(timestamp)))
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Ids_created_over_increasing_time_are_sorted_in_sql_server_order()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var ids = Enumerable.Range(0, 1000)
            .Select(i => new SqlGuid(SequentialGuid.Create(timestamp.AddMilliseconds(i))))
            .ToList();

        ids.ShouldBe(ids.OrderBy(id => id).ToList());
    }

    [Fact]
    public void Same_timestamp_produces_distinct_ids()
    {
        var timestamp = DateTimeOffset.UnixEpoch;

        Enumerable.Range(0, 1000)
            .Select(_ => SequentialGuid.Create(timestamp))
            .Distinct()
            .Count()
            .ShouldBe(1000);
    }
}
