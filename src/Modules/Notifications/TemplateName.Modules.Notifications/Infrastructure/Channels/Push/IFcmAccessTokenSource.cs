namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Push;

/// <summary>
/// The OAuth access token FCM's send endpoint takes (scope <c>firebase.messaging</c>), cached and refreshed by the implementation. A
/// failure throws <c>PushSendException</c> with the transient reason <c>fcm_auth_failed</c>.
/// </summary>
internal interface IFcmAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
