using Microsoft.AspNetCore.SignalR;

namespace TemplateName.Modules.Notifications.Infrastructure.Hubs;

/// <summary>
/// Names a hub connection's user by its <c>sub</c> claim, which both the JWT bearer scheme (inbound claims are not mapped) and the
/// <c>HubTicket</c> scheme set to the user id. The value is normalized to <see cref="Guid.ToString()"/>, the form the pusher addresses
/// (<c>Clients.User</c> compares ordinally); a connection without a usable <c>sub</c> belongs to no user and receives no push.
/// </summary>
internal sealed class SubClaimUserIdProvider : IUserIdProvider
{
    private const string SubjectClaimType = "sub";

    public string? GetUserId(HubConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return Guid.TryParse(connection.User?.FindFirst(SubjectClaimType)?.Value, out var userId) ? userId.ToString() : null;
    }
}
