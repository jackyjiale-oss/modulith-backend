using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Admin.Roles.Update;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class UpdateRoleCommandHandlerTests : AdminRoleHandlerTestBase
{
    private readonly UpdateRoleCommandHandler _sut;

    public UpdateRoleCommandHandlerTests()
    {
        _sut = new UpdateRoleCommandHandler(Roles, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Update_renames_the_role_audits_and_saves_on_the_name_index()
    {
        var role = GivenStoredRole("Support");

        var result = await _sut.HandleAsync(new UpdateRoleCommand(ActorId, role.Id, " Helpdesk ", "Answers tickets"), Ct);

        result.IsSuccess.ShouldBeTrue();
        role.Name.ShouldBe("Helpdesk");
        role.NormalizedName.ShouldBe("HELPDESK");
        role.Description.ShouldBe("Answers tickets");
        await Roles.Received(1).NameExistsAsync("HELPDESK", role.Id, Arg.Any<CancellationToken>());
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RoleUpdated);
        audit.UserId.ShouldBeNull();
        var details = DetailsOf(audit);
        details.GetProperty("actorId").GetGuid().ShouldBe(ActorId);
        details.GetProperty("roleId").GetGuid().ShouldBe(role.Id);
        details.GetProperty("name").GetString().ShouldBe("Helpdesk");
        details.GetProperty("previousName").GetString().ShouldBe("Support");
        await UnitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(UniqueIndexNames.RoleName, Arg.Any<CancellationToken>());

        // A rename does not change a grant, so no user's permissions are reloaded.
        await PermissionCache.DidNotReceiveWithAnyArgs().InvalidateUsersAsync(default!, Ct);
    }

    [Theory]
    [InlineData(SystemRoles.SuperAdmin)]
    [InlineData(SystemRoles.Admin)]
    [InlineData(SystemRoles.User)]
    public async Task A_system_role_cannot_be_renamed(string name)
    {
        var role = GivenStoredSystemRole(name);

        var result = await _sut.HandleAsync(new UpdateRoleCommand(ActorId, role.Id, "Other", "Other"), Ct);

        result.Error.Code.ShouldBe("auth.system_role_protected");
        role.Name.ShouldBe(name);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_name_used_by_another_role_returns_role_name_taken()
    {
        var role = GivenStoredRole("Support");
        Roles.NameExistsAsync("HELPDESK", role.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(new UpdateRoleCommand(ActorId, role.Id, "Helpdesk", "x"), Ct);

        result.Error.Code.ShouldBe("auth.role_name_taken");
        result.Error.Parameters!["name"].ShouldBe("Helpdesk");
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
    }

    [Fact]
    public async Task Losing_the_unique_index_race_returns_role_name_taken()
    {
        var role = GivenStoredRole("Support");
        UnitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.RoleName, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new UpdateRoleCommand(ActorId, role.Id, "Helpdesk", "x"), Ct);

        result.Error.Code.ShouldBe("auth.role_name_taken");
    }

    [Fact]
    public async Task Unknown_or_deleted_role_returns_role_not_found()
    {
        var unknown = Guid.NewGuid();

        var result = await _sut.HandleAsync(new UpdateRoleCommand(ActorId, unknown, "Helpdesk", "x"), Ct);

        result.Error.Code.ShouldBe("auth.role_not_found");
        result.Error.Type.ShouldBe(ErrorType.NotFound);
        result.Error.Parameters!["id"].ShouldBe(unknown);
    }
}
