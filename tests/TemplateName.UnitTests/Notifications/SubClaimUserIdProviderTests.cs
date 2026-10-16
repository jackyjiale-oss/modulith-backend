using System.Security.Claims;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using TemplateName.Modules.Notifications.Infrastructure.Hubs;

namespace TemplateName.UnitTests.Notifications;

public sealed class SubClaimUserIdProviderTests
{
    [Fact]
    public void User_id_is_the_sub_claim_in_the_form_the_pusher_addresses()
    {
        var userId = Guid.NewGuid();

        var fromLowerCase = UserIdOf(new Claim("sub", userId.ToString()));
        var fromUpperCase = UserIdOf(new Claim("sub", userId.ToString().ToUpperInvariant()));

        fromLowerCase.ShouldBe(userId.ToString());
        fromUpperCase.ShouldBe(userId.ToString());
    }

    [Fact]
    public void Connection_without_a_usable_sub_has_no_user()
    {
        UserIdOf().ShouldBeNull();
        UserIdOf(new Claim("sub", "not-a-guid")).ShouldBeNull();
        UserIdOf(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())).ShouldBeNull();
    }

    private static string? UserIdOf(params Claim[] claims)
    {
        var transport = new DefaultConnectionContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        var connection = new HubConnectionContext(transport, new HubConnectionContextOptions(), NullLoggerFactory.Instance);

        return new SubClaimUserIdProvider().GetUserId(connection);
    }
}
