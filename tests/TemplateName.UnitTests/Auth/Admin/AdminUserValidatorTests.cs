using FluentValidation.TestHelper;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;
using TemplateName.Modules.Auth.Application.Admin.Users.Create;
using TemplateName.Modules.Auth.Application.Admin.Users.List;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class AdminUserValidatorTests
{
    private static readonly Guid ActorId = Guid.NewGuid();

    private readonly CreateUserCommandValidator _create = new();
    private readonly AssignRolesCommandValidator _assign = new();
    private readonly ListUsersQueryValidator _list = new();

    [Fact]
    public void A_complete_create_command_is_valid()
        => _create.TestValidate(new CreateUserCommand(ActorId, "alice@example.com", "Alice", "zh-Hans", null)).ShouldNotHaveAnyValidationErrors();

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public void Create_requires_an_email_address(string email)
        => _create.TestValidate(new CreateUserCommand(ActorId, email, "Alice", "en", null)).ShouldHaveValidationErrorFor(command => command.Email);

    [Fact]
    public void Create_refuses_an_email_longer_than_the_column()
    {
        var email = new string('a', User.MaxEmailLength - "@example.com".Length + 1) + "@example.com";

        _create.TestValidate(new CreateUserCommand(ActorId, email, "Alice", "en", null)).ShouldHaveValidationErrorFor(command => command.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_requires_a_display_name(string displayName)
        => _create.TestValidate(new CreateUserCommand(ActorId, "alice@example.com", displayName, "en", null))
            .ShouldHaveValidationErrorFor(command => command.DisplayName);

    [Theory]
    [InlineData("klingon")]
    [InlineData("")]
    public void Create_requires_a_known_locale(string locale)
        => _create.TestValidate(new CreateUserCommand(ActorId, "alice@example.com", "Alice", locale, null))
            .ShouldHaveValidationErrorFor(command => command.Locale);

    [Fact]
    public void Create_refuses_an_empty_role_id_and_too_many_roles()
    {
        _create.TestValidate(new CreateUserCommand(ActorId, "alice@example.com", "Alice", "en", [Guid.Empty]))
            .ShouldHaveValidationErrorFor(command => command.RoleIds);
        _create.TestValidate(new CreateUserCommand(ActorId, "alice@example.com", "Alice", "en", [.. Enumerable.Range(0, 51).Select(_ => Guid.NewGuid())]))
            .ShouldHaveValidationErrorFor(command => command.RoleIds);
    }

    [Fact]
    public void Assign_requires_a_set_without_empty_ids_and_accepts_an_empty_one()
    {
        _assign.TestValidate(new AssignRolesCommand(ActorId, Guid.NewGuid(), [])).ShouldNotHaveAnyValidationErrors();
        _assign.TestValidate(new AssignRolesCommand(ActorId, Guid.NewGuid(), null!)).ShouldHaveValidationErrorFor(command => command.RoleIds);
        _assign.TestValidate(new AssignRolesCommand(ActorId, Guid.NewGuid(), [Guid.Empty])).ShouldHaveValidationErrorFor(command => command.RoleIds);
    }

    [Fact]
    public void List_limits_the_page_size_the_search_length_and_the_status()
    {
        _list.TestValidate(new ListUsersQuery("alice", UserStatus.Active, new CursorPageRequest(50, null, null, false))).ShouldNotHaveAnyValidationErrors();
        _list.TestValidate(new ListUsersQuery(null, null, new CursorPageRequest(0, null, null, false)))
            .ShouldHaveValidationErrorFor(nameof(CursorPageRequest.PageSize));
        _list.TestValidate(new ListUsersQuery(new string('a', User.MaxEmailLength + 1), null, new CursorPageRequest(20, null, null, false)))
            .ShouldHaveValidationErrorFor(query => query.Search);
        _list.TestValidate(new ListUsersQuery(null, (UserStatus)7, new CursorPageRequest(20, null, null, false)))
            .ShouldHaveValidationErrorFor(query => query.Status);
    }

    [Theory]
    [InlineData("alice", "ALICE%")]
    [InlineData(" Bob@Example ", "BOB@EXAMPLE%")]
    [InlineData("50%_off[1]\\", @"50\%\_OFF\[1]\\%")]
    public void Search_becomes_an_escaped_prefix_of_the_normalized_email(string search, string pattern)
        => ListUsersQueryHandler.ToPrefixPattern(search).ShouldBe(pattern);
}
