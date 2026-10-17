using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Notifications.Application.Inbox.List;

namespace TemplateName.UnitTests.Notifications.Inbox;

public sealed class ListInboxQueryValidatorTests
{
    private readonly ListInboxQueryValidator _sut = new();

    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    [InlineData(-5, false)]
    public void Page_size_must_be_between_1_and_100(int pageSize, bool valid)
    {
        var result = _sut.Validate(new ListInboxQuery(new CursorPageRequest(pageSize), UnreadOnly: false));

        result.IsValid.ShouldBe(valid);
        if (!valid)
        {
            // Reported under the query-string name, not the nested path.
            result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("PageSize");
        }
    }

    [Fact]
    public void The_sort_allow_list_has_created_at_only_and_defaults_to_newest_first()
    {
        InAppNotificationSortFields.Allowed.Select(field => field.Name).ShouldBe(["createdAt"]);
        InAppNotificationSortFields.Default.ShouldBe("-createdAt");
    }
}
