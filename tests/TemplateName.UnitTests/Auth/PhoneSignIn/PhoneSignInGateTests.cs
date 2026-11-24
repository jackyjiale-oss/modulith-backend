using Microsoft.Extensions.Options;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.PhoneSignIn;
using TemplateName.Modules.Auth.Domain.Roles;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.UnitTests.Auth.PhoneSignIn;

public sealed class PhoneSignInGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    private readonly IRoleRepository _roles = Substitute.For<IRoleRepository>();
    private readonly IMfaMethodRepository _mfaMethods = Substitute.For<IMfaMethodRepository>();
    private readonly PhoneOptions _options = new();
    private readonly Role _admin = Role.CreateSystem("Admin", "Administrators", Now.AddDays(-30));
    private readonly Role _user = Role.CreateSystem("User", "Users", Now.AddDays(-30));

    public PhoneSignInGateTests()
    {
        _roles.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { _admin, _user }.Where(role => call.Arg<IReadOnlyCollection<Guid>>().Contains(role.Id)).ToList());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Admin_without_mfa_is_denied() =>
        (await Gate().IsDeniedAsync(UserIn(_admin), Ct)).ShouldBeTrue();

    [Fact]
    public async Task Admin_with_mfa_is_not_denied()
    {
        var user = UserIn(_admin);
        _mfaMethods.HasConfirmedAsync(user.Id, Arg.Any<CancellationToken>()).Returns(true);

        (await Gate().IsDeniedAsync(user, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task User_role_is_not_denied() =>
        (await Gate().IsDeniedAsync(UserIn(_user), Ct)).ShouldBeFalse();

    [Fact]
    public async Task Denied_roles_compare_normalized_names()
    {
        _options.SignInDeniedRoles = [" admin "];

        (await Gate().IsDeniedAsync(UserIn(_admin), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_list_of_names_no_role_has_denies_nobody()
    {
        _options.SignInDeniedRoles = ["Nobody"];

        (await Gate().IsDeniedAsync(UserIn(_admin), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_user_without_roles_is_not_denied_and_reads_nothing()
    {
        var user = User.Register("bob@example.com", "Bob", "en", "hash", Now.AddDays(-1)).Value;

        (await Gate().IsDeniedAsync(user, Ct)).ShouldBeFalse();
        await _roles.DidNotReceiveWithAnyArgs().GetByIdsAsync(default!, Ct);
    }

    private PhoneSignInGate Gate() => new(_roles, _mfaMethods, Options.Create(_options));

    private static User UserIn(Role role)
    {
        var user = User.Register("alice@example.com", "Alice", "en", "hash", Now.AddDays(-1)).Value;
        user.AssignRole(role.Id, assignedBy: null, Now.AddDays(-1));
        return user;
    }
}
