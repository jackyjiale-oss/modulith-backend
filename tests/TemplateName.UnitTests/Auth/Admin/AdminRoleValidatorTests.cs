using FluentValidation.TestHelper;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;
using TemplateName.Modules.Auth.Application.Admin.Permissions.List;
using TemplateName.Modules.Auth.Application.Admin.Roles.Create;
using TemplateName.Modules.Auth.Application.Admin.Roles.List;
using TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;
using TemplateName.Modules.Auth.Application.Admin.Roles.Update;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class AdminRoleValidatorTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid RoleId = Guid.NewGuid();

    private readonly CreateRoleCommandValidator _create = new();
    private readonly UpdateRoleCommandValidator _update = new();
    private readonly SetRolePermissionsCommandValidator _setPermissions = new();
    private readonly ListRolesQueryValidator _listRoles = new();
    private readonly ListPermissionsQueryValidator _listPermissions = new();
    private readonly ListAuditLogsQueryValidator _listAuditLogs = new();

    [Fact]
    public void A_complete_role_is_valid()
    {
        _create.TestValidate(new CreateRoleCommand(ActorId, "Support", "Handles tickets")).ShouldNotHaveAnyValidationErrors();
        _update.TestValidate(new UpdateRoleCommand(ActorId, RoleId, "Support", string.Empty)).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_role_needs_a_name(string? name)
    {
        _create.TestValidate(new CreateRoleCommand(ActorId, name!, "x")).ShouldHaveValidationErrorFor(command => command.Name);
        _update.TestValidate(new UpdateRoleCommand(ActorId, RoleId, name!, "x")).ShouldHaveValidationErrorFor(command => command.Name);
    }

    [Fact]
    public void A_role_name_fits_its_column()
    {
        _create.TestValidate(new CreateRoleCommand(ActorId, new string('n', Role.MaxNameLength), "x")).ShouldNotHaveValidationErrorFor(command => command.Name);
        _create.TestValidate(new CreateRoleCommand(ActorId, new string('n', Role.MaxNameLength + 1), "x")).ShouldHaveValidationErrorFor(command => command.Name);
        _update.TestValidate(new UpdateRoleCommand(ActorId, RoleId, new string('n', Role.MaxNameLength + 1), "x")).ShouldHaveValidationErrorFor(command => command.Name);
    }

    [Fact]
    public void A_role_description_is_required_but_may_be_empty_and_fits_its_column()
    {
        _create.TestValidate(new CreateRoleCommand(ActorId, "Support", null!)).ShouldHaveValidationErrorFor(command => command.Description);
        _update.TestValidate(new UpdateRoleCommand(ActorId, RoleId, "Support", null!)).ShouldHaveValidationErrorFor(command => command.Description);
        _create.TestValidate(new CreateRoleCommand(ActorId, "Support", string.Empty)).ShouldNotHaveValidationErrorFor(command => command.Description);
        _create.TestValidate(new CreateRoleCommand(ActorId, "Support", new string('d', Role.MaxDescriptionLength)))
            .ShouldNotHaveValidationErrorFor(command => command.Description);
        _create.TestValidate(new CreateRoleCommand(ActorId, "Support", new string('d', Role.MaxDescriptionLength + 1)))
            .ShouldHaveValidationErrorFor(command => command.Description);
    }

    [Fact]
    public void Permission_ids_are_a_set_without_empty_ids_and_may_be_empty()
    {
        _setPermissions.TestValidate(new SetRolePermissionsCommand(ActorId, RoleId, [])).ShouldNotHaveAnyValidationErrors();
        _setPermissions.TestValidate(new SetRolePermissionsCommand(ActorId, RoleId, null!)).ShouldHaveValidationErrorFor(command => command.PermissionIds);
        _setPermissions.TestValidate(new SetRolePermissionsCommand(ActorId, RoleId, [Guid.Empty])).ShouldHaveValidationErrorFor(command => command.PermissionIds);
        _setPermissions.TestValidate(new SetRolePermissionsCommand(
            ActorId,
            RoleId,
            [.. Enumerable.Range(0, SetRolePermissionsCommandValidator.MaxPermissions + 1).Select(_ => Guid.NewGuid())]))
            .ShouldHaveValidationErrorFor(command => command.PermissionIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(CursorPageRequest.MaxPageSize + 1)]
    public void The_lists_cap_the_page_size(int pageSize)
    {
        var page = new CursorPageRequest(PageSize: pageSize);

        _listRoles.TestValidate(new ListRolesQuery(page)).ShouldHaveValidationErrorFor("PageSize");
        _listPermissions.TestValidate(new ListPermissionsQuery(IncludeDeprecated: false, page)).ShouldHaveValidationErrorFor("PageSize");
        _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, null, null, page)).ShouldHaveValidationErrorFor("PageSize");
    }

    [Fact]
    public void The_lists_accept_the_default_and_the_largest_page()
    {
        foreach (var size in new[] { CursorPageRequest.DefaultPageSize, CursorPageRequest.MaxPageSize })
        {
            var page = new CursorPageRequest(PageSize: size);
            _listRoles.TestValidate(new ListRolesQuery(page)).ShouldNotHaveAnyValidationErrors();
            _listPermissions.TestValidate(new ListPermissionsQuery(IncludeDeprecated: true, page)).ShouldNotHaveAnyValidationErrors();
            _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, null, null, page)).ShouldNotHaveAnyValidationErrors();
        }
    }

    [Fact]
    public void The_audit_event_type_must_be_one_the_log_writes()
    {
        foreach (var eventType in AuthAuditEvents.All)
        {
            _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, eventType, null, null, null, new CursorPageRequest()))
                .ShouldNotHaveValidationErrorFor(query => query.EventType);
        }

        foreach (var eventType in new[] { "auth.nothing", "AUTH.LOGOUT", "'; DROP TABLE x; --", " auth.logout" })
        {
            _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, eventType, null, null, null, new CursorPageRequest()))
                .ShouldHaveValidationErrorFor(query => query.EventType);
        }
    }

    [Theory]
    [InlineData("2026-10-09T12:30:00Z")]
    [InlineData("2026-10-09T20:30:00+08:00")]
    [InlineData("2026-10-09")]
    [InlineData("")]
    [InlineData("   ")]
    public void The_audit_dates_accept_iso_8601_values_and_treat_a_blank_one_as_no_filter(string value)
        => _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, value, value, new CursorPageRequest())).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-45")]
    public void The_audit_dates_refuse_anything_else(string value)
    {
        var result = _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, value, value, new CursorPageRequest()));

        result.ShouldHaveValidationErrorFor(query => query.From);
        result.ShouldHaveValidationErrorFor(query => query.To);
    }

    [Fact]
    public void The_audit_range_must_not_end_before_it_starts_comparing_instants_in_utc()
    {
        var page = new CursorPageRequest();

        // The same instant written with two offsets is an empty-but-valid range; one second earlier is not.
        _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, "2026-10-09T08:00:00+08:00", "2026-10-09T00:00:00Z", page))
            .ShouldNotHaveAnyValidationErrors();
        _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, "2026-10-09T08:00:01+08:00", "2026-10-09T00:00:00Z", page))
            .ShouldHaveValidationErrorFor(query => query.To);
        _listAuditLogs.TestValidate(new ListAuditLogsQuery(null, null, null, "2026-10-09T00:00:00Z", null, page)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void An_audit_date_without_an_offset_is_read_as_utc()
    {
        AuditLogTimestamp.TryParse("2026-10-09T08:00:00", out var utc).ShouldBeTrue();
        utc.ShouldBe(new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc));
        AuditLogTimestamp.TryParse("2026-10-09T08:00:00+08:00", out utc).ShouldBeTrue();
        utc.ShouldBe(new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        utc.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
