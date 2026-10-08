using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using TemplateName.Web.Common.Security;

namespace TemplateName.UnitTests.Web;

public sealed class PermissionEndpointExtensionsTests
{
    [Fact]
    public void Require_permission_adds_a_policy_with_an_authenticated_user_and_the_permission()
    {
        var conventions = new RecordingEndpointConventionBuilder();

        conventions.RequirePermission("auth.user.view").ShouldBeSameAs(conventions);

        var endpoint = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/test"), order: 0);
        conventions.Apply(endpoint);
        var policy = endpoint.Metadata.OfType<AuthorizationPolicy>().ShouldHaveSingleItem();
        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
        policy.Requirements.OfType<PermissionRequirement>().ShouldHaveSingleItem().Permission.ShouldBe("auth.user.view");
    }

    private sealed class RecordingEndpointConventionBuilder : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];

        public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

        public void Apply(EndpointBuilder endpoint)
        {
            foreach (var convention in _conventions)
            {
                convention(endpoint);
            }
        }
    }
}
