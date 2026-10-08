using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Infrastructure.Authorization;

namespace TemplateName.UnitTests.Auth;

public sealed class PermissionSynchronizerTests
{
    [Fact]
    public void Valid_definitions_from_every_source_pass_in_declaration_order()
    {
        var other = new PermissionDefinition("sample.leave_request.approve", "sample", "Approve leave", "Approve a leave request.");

        var definitions = PermissionSynchronizer.Validate([new AuthPermissionSource(), new StubPermissionSource(other)]);

        definitions.Count.ShouldBe(11);
        definitions[^1].ShouldBe(other);
    }

    [Theory]
    [InlineData("Auth.user.view", "auth", "upper case")]
    [InlineData("auth.user", "auth", "two segments")]
    [InlineData("auth.user.view.all", "auth", "four segments")]
    [InlineData("auth.user.view\n", "auth", "trailing newline")]
    [InlineData("auth.user-account.view", "auth", "kebab case")]
    [InlineData("auth.user.view2", "auth", "digit")]
    [InlineData("auth_x.user.view", "auth_x", "underscore in the module")]
    [InlineData("", "", "empty")]
    public void Code_that_is_not_snake_case_module_resource_action_fails_naming_the_code(string code, string module, string reason)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => PermissionSynchronizer.Validate([new StubPermissionSource(new PermissionDefinition(code, module, "Name", "Description"))]),
            reason);

        exception.Message.ShouldContain($"'{code}'");
        exception.Message.ShouldContain(nameof(StubPermissionSource));
    }

    [Fact]
    public void Module_that_is_not_the_first_segment_of_the_code_fails_naming_the_code()
    {
        var exception = Should.Throw<InvalidOperationException>(() => PermissionSynchronizer.Validate(
            [new StubPermissionSource(new PermissionDefinition("auth.user.view", "sample", "Name", "Description"))]));

        exception.Message.ShouldContain("'auth.user.view'");
        exception.Message.ShouldContain("module");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_name_fails_naming_the_code(string name)
    {
        var exception = Should.Throw<InvalidOperationException>(() => PermissionSynchronizer.Validate(
            [new StubPermissionSource(new PermissionDefinition("auth.user.view", "auth", name, "Description"))]));

        exception.Message.ShouldContain("'auth.user.view'");
        exception.Message.ShouldContain("name");
    }

    [Fact]
    public void Values_longer_than_their_columns_fail_naming_the_code()
    {
        var longName = new PermissionDefinition("auth.user.view", "auth", new string('n', 201), "Description");
        var longDescription = new PermissionDefinition("auth.user.view", "auth", "Name", new string('d', 501));
        var longCode = new PermissionDefinition("auth.user." + new string('v', 120), "auth", "Name", "Description");

        foreach (var definition in new[] { longName, longDescription, longCode })
        {
            Should.Throw<InvalidOperationException>(() => PermissionSynchronizer.Validate([new StubPermissionSource(definition)]))
                .Message.ShouldContain($"'{definition.Code}'");
        }
    }

    [Fact]
    public void Null_description_and_null_definition_fail()
    {
        Should.Throw<InvalidOperationException>(() => PermissionSynchronizer.Validate(
            [new StubPermissionSource(new PermissionDefinition("auth.user.view", "auth", "Name", null!))]))
            .Message.ShouldContain("'auth.user.view'");
        Should.Throw<InvalidOperationException>(() => PermissionSynchronizer.Validate([new StubPermissionSource([null!])]))
            .Message.ShouldContain(nameof(StubPermissionSource));
    }

    [Fact]
    public void Code_declared_twice_fails_naming_the_code_even_across_sources()
    {
        var duplicate = new PermissionDefinition("auth.user.view", "auth", "Another name", "Another description");

        var exception = Should.Throw<InvalidOperationException>(
            () => PermissionSynchronizer.Validate([new AuthPermissionSource(), new StubPermissionSource(duplicate)]));

        exception.Message.ShouldContain("'auth.user.view'");
        exception.Message.ShouldContain(nameof(AuthPermissionSource));
        exception.Message.ShouldContain(nameof(StubPermissionSource));
    }

    private sealed class StubPermissionSource(params PermissionDefinition[] permissions) : IPermissionSource
    {
        public IReadOnlyCollection<PermissionDefinition> Permissions { get; } = permissions;
    }
}
