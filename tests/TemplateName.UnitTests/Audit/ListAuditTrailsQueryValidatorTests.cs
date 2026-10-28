using FluentValidation.TestHelper;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Audit.Application.Trails.List;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.UnitTests.Audit;

public sealed class ListAuditTrailsQueryValidatorTests
{
    private readonly ListAuditTrailsQueryValidator _validator = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CursorPageRequest.MaxPageSize + 1)]
    public void A_page_size_outside_one_to_the_maximum_is_refused(int pageSize)
        => _validator.TestValidate(QueryWith(new CursorPageRequest(PageSize: pageSize))).ShouldHaveValidationErrorFor("PageSize");

    [Theory]
    [InlineData(1)]
    [InlineData(CursorPageRequest.DefaultPageSize)]
    [InlineData(CursorPageRequest.MaxPageSize)]
    public void A_page_size_from_one_to_the_maximum_is_accepted(int pageSize)
        => _validator.TestValidate(QueryWith(new CursorPageRequest(PageSize: pageSize))).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void No_filter_at_all_is_valid()
        => _validator.TestValidate(QueryWith(new CursorPageRequest())).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void The_action_must_be_the_exact_name_of_an_entity_change_action()
    {
        foreach (var name in Enum.GetNames<EntityChangeAction>())
        {
            _validator.TestValidate(QueryWith(new CursorPageRequest(), action: name)).ShouldNotHaveValidationErrorFor(query => query.Action);
        }

        foreach (var action in new[] { "created", "CREATED", "Create", "Nothing", " Created", "Updated ", "1", "'; DROP TABLE x; --" })
        {
            _validator.TestValidate(QueryWith(new CursorPageRequest(), action: action)).ShouldHaveValidationErrorFor(query => query.Action);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_action_is_no_filter(string? action)
        => _validator.TestValidate(QueryWith(new CursorPageRequest(), action: action)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("2026-10-09T12:30:00Z")]
    [InlineData("2026-10-09T20:30:00+08:00")]
    [InlineData("2026-10-09T12:30:00.5")]
    [InlineData("2026-10-09")]
    [InlineData("")]
    [InlineData("   ")]
    public void The_dates_accept_iso_8601_values_and_treat_a_blank_one_as_no_filter(string value)
        => _validator.TestValidate(QueryWith(new CursorPageRequest(), from: value, to: value)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-45")]
    [InlineData("09/10/2026")]
    [InlineData("2026-10-09 12:30:00")]
    public void The_dates_refuse_anything_else(string value)
    {
        var result = _validator.TestValidate(QueryWith(new CursorPageRequest(), from: value, to: value));

        result.ShouldHaveValidationErrorFor(query => query.From);
        result.ShouldHaveValidationErrorFor(query => query.To);
    }

    [Fact]
    public void The_range_must_not_end_before_it_starts_comparing_instants_in_utc()
    {
        var page = new CursorPageRequest();

        // The same instant written with two offsets is a valid range of one moment; one second earlier is not.
        _validator.TestValidate(QueryWith(page, from: "2026-10-09T08:00:00+08:00", to: "2026-10-09T00:00:00Z")).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(QueryWith(page, from: "2026-10-09", to: "2026-10-09")).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(QueryWith(page, from: "2026-10-09T00:00:00Z")).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(QueryWith(page, to: "2026-10-09T00:00:00Z")).ShouldNotHaveAnyValidationErrors();

        var result = _validator.TestValidate(QueryWith(page, from: "2026-10-09T08:00:01+08:00", to: "2026-10-09T00:00:00Z"));
        result.ShouldHaveValidationErrorFor(query => query.To).WithErrorMessage("'To' must not be earlier than 'From'.");
        result.ShouldNotHaveValidationErrorFor(query => query.From);
    }

    [Fact]
    public void A_date_without_an_offset_is_read_as_utc()
    {
        AuditTrailTimestamp.TryParse("2026-10-09T08:00:00", out var utc).ShouldBeTrue();
        utc.ShouldBe(new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc));
        AuditTrailTimestamp.TryParse("2026-10-09T08:00:00+08:00", out utc).ShouldBeTrue();
        utc.ShouldBe(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        utc.Kind.ShouldBe(DateTimeKind.Utc);
        AuditTrailTimestamp.TryParse("yesterday", out _).ShouldBeFalse();
        AuditTrailTimestamp.TryParse(null, out _).ShouldBeFalse();
    }

    private static ListAuditTrailsQuery QueryWith(CursorPageRequest page, string? action = null, string? from = null, string? to = null)
        => new(page, EntityType: null, EntityId: null, UserId: null, ChangeSetId: null, Module: null, action, from, to);
}
