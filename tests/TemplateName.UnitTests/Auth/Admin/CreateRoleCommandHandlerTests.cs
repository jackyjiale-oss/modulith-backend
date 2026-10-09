using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Admin.Roles.Create;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class CreateRoleCommandHandlerTests : AdminRoleHandlerTestBase
{
    private readonly CreateRoleCommandHandler _sut;

    public CreateRoleCommandHandlerTests()
    {
        _sut = new CreateRoleCommandHandler(Roles, AuditWriter, UnitOfWork, Time);
    }

    [Fact]
    public async Task Create_adds_the_role_audits_with_the_actor_and_saves_once_on_the_name_index()
    {
        Role? added = null;
        Roles.When(roles => roles.Add(Arg.Any<Role>())).Do(call => added = call.Arg<Role>());

        var result = await _sut.HandleAsync(new CreateRoleCommand(ActorId, "  Support Agent ", "Handles tickets"), Ct);

        result.IsSuccess.ShouldBeTrue();
        added.ShouldNotBeNull();
        result.Value.ShouldBe(added.Id);
        added.Name.ShouldBe("Support Agent");
        added.Description.ShouldBe("Handles tickets");
        added.IsSystem.ShouldBeFalse();
        added.Permissions.ShouldBeEmpty();
        await Roles.Received(1).NameExistsAsync("SUPPORT AGENT", null, Arg.Any<CancellationToken>());
        var audit = AuditEntries.ShouldHaveSingleItem();
        audit.EventType.ShouldBe(AuthAuditEvents.RoleCreated);
        audit.Succeeded.ShouldBeTrue();
        audit.UserId.ShouldBeNull();
        audit.OccurredAt.ShouldBe(Now);
        var details = DetailsOf(audit);
        details.GetProperty("actorId").GetGuid().ShouldBe(ActorId);
        details.GetProperty("roleId").GetGuid().ShouldBe(added.Id);
        details.GetProperty("name").GetString().ShouldBe("Support Agent");
        await UnitOfWork.Received(1).SaveChangesUnlessDuplicateAsync(UniqueIndexNames.RoleName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_name_in_use_in_any_case_returns_role_name_taken_and_adds_nothing()
    {
        Roles.NameExistsAsync("SUPPORT", null, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HandleAsync(new CreateRoleCommand(ActorId, "support", "Again"), Ct);

        result.Error.Code.ShouldBe("auth.role_name_taken");
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        result.Error.Parameters!["name"].ShouldBe("support");
        Roles.DidNotReceiveWithAnyArgs().Add(default!);
        AuditEntries.ShouldBeEmpty();
        await UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesUnlessDuplicateAsync(default!, Ct);
    }

    [Fact]
    public async Task Losing_the_unique_index_race_returns_role_name_taken()
    {
        // A simultaneous create passed the same lookup; the index let the other insert win.
        UnitOfWork.SaveChangesUnlessDuplicateAsync(UniqueIndexNames.RoleName, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.HandleAsync(new CreateRoleCommand(ActorId, "Support", "Tickets"), Ct);

        result.Error.Code.ShouldBe("auth.role_name_taken");
    }
}
