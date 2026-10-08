using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Web.Common.Security;

namespace TemplateName.UnitTests.Web;

public sealed class PermissionAuthorizationHandlerTests
{
    private const string GrantedPermission = "auth.user.view";
    private const string MissingPermission = "auth.role.manage";

    private static readonly Guid UserId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IPermissionChecker _permissionChecker = Substitute.For<IPermissionChecker>();

    public PermissionAuthorizationHandlerTests()
    {
        _permissionChecker.HasPermissionAsync(UserId, GrantedPermission, Arg.Any<CancellationToken>()).Returns(true);
        _permissionChecker.HasPermissionAsync(UserId, MissingPermission, Arg.Any<CancellationToken>()).Returns(false);
    }

    [Fact]
    public async Task Handler_fails_for_anonymous_user()
    {
        _currentUser.IsAuthenticated.Returns(false);
        _currentUser.UserId.Returns((Guid?)null);
        _permissionChecker.HasPermissionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var context = await AuthorizeAsync(GrantedPermission);

        context.HasSucceeded.ShouldBeFalse();
        await _permissionChecker.DidNotReceive().HasPermissionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_succeeds_only_when_checker_confirms_the_permission()
    {
        SignIn();

        var granted = await AuthorizeAsync(GrantedPermission);
        var missing = await AuthorizeAsync(MissingPermission);

        granted.HasSucceeded.ShouldBeTrue();
        missing.HasSucceeded.ShouldBeFalse();
        await _permissionChecker.Received(1).HasPermissionAsync(UserId, GrantedPermission, Arg.Any<CancellationToken>());
        await _permissionChecker.Received(1).HasPermissionAsync(UserId, MissingPermission, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_fails_for_an_authenticated_user_without_a_user_id()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns((Guid?)null);

        var context = await AuthorizeAsync(GrantedPermission);

        context.HasSucceeded.ShouldBeFalse();
        await _permissionChecker.DidNotReceive().HasPermissionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handler_lets_a_checker_failure_propagate_instead_of_granting()
    {
        SignIn();
        _permissionChecker.HasPermissionAsync(UserId, GrantedPermission, Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("database unavailable"));

        var context = new AuthorizationHandlerContext([new PermissionRequirement(GrantedPermission)], SignedInPrincipal(), resource: null);

        await Should.ThrowAsync<InvalidOperationException>(() => CreateHandler().HandleAsync(context));
        context.HasSucceeded.ShouldBeFalse();
    }

    private void SignIn()
    {
        _currentUser.IsAuthenticated.Returns(true);
        _currentUser.UserId.Returns(UserId);
    }

    private async Task<AuthorizationHandlerContext> AuthorizeAsync(string permission)
    {
        var context = new AuthorizationHandlerContext([new PermissionRequirement(permission)], SignedInPrincipal(), resource: null);
        await CreateHandler().HandleAsync(context);
        return context;
    }

    private PermissionAuthorizationHandler CreateHandler() => new(_currentUser, _permissionChecker);

    private static ClaimsPrincipal SignedInPrincipal()
        => new(new ClaimsIdentity([new Claim("sub", UserId.ToString())], authenticationType: "Test"));
}
