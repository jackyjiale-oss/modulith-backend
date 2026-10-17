using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.HubTickets;

namespace TemplateName.Modules.Notifications.Infrastructure.Hubs;

/// <summary>
/// The <c>HubTicket</c> scheme (D12, ADR 0021). It looks only at requests below <see cref="HubTicketAuthenticationOptions.PathPrefix"/>
/// (<c>/hubs/notifications</c>, case-insensitive); everywhere else it answers no result without reading anything, so a ticket
/// authenticates nothing but the hub. The ticket comes from the <c>access_token</c> query parameter (browsers, which cannot set headers on
/// a WebSocket) or else from <c>Authorization: Bearer</c> (negotiate, and clients that can set headers). It is consumed with one
/// conditional update (<see cref="IHubTicketRepository.TryConsumeAsync"/>) at the <see cref="TimeProvider"/>'s now: the principal holds
/// only <c>sub</c> (the user id) and expires <see cref="HubOptions.ConnectionLifetime"/> later, which closes the connection
/// (<c>CloseOnAuthenticationExpiration</c>). An unknown, used or expired ticket fails with one message that does not say which.
/// </summary>
/// <remarks>
/// The authentication middleware runs a scheme once per request and caches the result, so a ticket is consumed at most once per
/// request; a connection makes two requests (negotiate, then the WebSocket) and the client fetches a ticket for each. A bearer header
/// that is not shaped like a ticket belongs to the JWT scheme, which the hub accepts too, so it is no result here; a query value that is
/// not a ticket (a JWT, for one) fails, because the query is never read for a JWT. No value is ever logged.
/// </remarks>
internal sealed class HubTicketAuthenticationHandler(
    IOptionsMonitor<HubTicketAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHubTicketRepository tickets,
    TimeProvider timeProvider,
    IOptions<HubOptions> hubOptions)
    : AuthenticationHandler<HubTicketAuthenticationOptions>(options, logger, encoder)
{
    /// <summary>The failure message for every refused ticket; it names no value and does not say why.</summary>
    internal const string FailureMessage = "The hub ticket is not valid.";

    private const string SubjectClaimType = "sub";
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments(Options.PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var queryValues = Request.Query[HubTicketAuthenticationOptions.QueryParameterName];
        if (queryValues.Count > 1)
        {
            return AuthenticateResult.Fail(FailureMessage);
        }

        byte[]? hash;
        var queryValue = queryValues.Count == 1 ? queryValues[0] : null;
        if (!string.IsNullOrEmpty(queryValue))
        {
            if (!HubTicketTokens.TryHash(queryValue, out hash))
            {
                return AuthenticateResult.Fail(FailureMessage);
            }
        }
        else
        {
            var authorization = Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
                || !HubTicketTokens.TryHash(authorization[BearerPrefix.Length..].Trim(), out hash))
            {
                return AuthenticateResult.NoResult();
            }
        }

        var now = timeProvider.GetUtcNow();
        if (await tickets.TryConsumeAsync(hash, now, Context.RequestAborted) is not { } userId)
        {
            return AuthenticateResult.Fail(FailureMessage);
        }

        var identity = new ClaimsIdentity([new Claim(SubjectClaimType, userId.ToString())], Scheme.Name, SubjectClaimType, roleType: null);
        var properties = new AuthenticationProperties { ExpiresUtc = now + hubOptions.Value.ConnectionLifetime, IsPersistent = false };

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme.Name));
    }
}
