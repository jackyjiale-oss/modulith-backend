using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Push;

/// <summary>
/// <see cref="IFcmAccessTokenSource"/> with <c>Google.Apis.Auth</c>: the service account of <see cref="FcmOptions.CredentialsJson"/>, or
/// Google's application default credentials when it is empty, scoped to <c>firebase.messaging</c>. The credential is built once and
/// caches and refreshes its token itself. Any failure (unreadable credentials, the token endpoint down) is the transient
/// <c>fcm_auth_failed</c>; the exception is not attached, since its message may quote the credentials.
/// </summary>
internal sealed class GoogleFcmAccessTokenSource(IOptions<FcmOptions> options) : IFcmAccessTokenSource
{
    internal const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITokenAccess? _credential;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credential = await CredentialAsync(cancellationToken);
            return await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new PushSendException(isPermanent: false, "fcm_auth_failed", deactivateDevice: false);
        }
    }

    private async Task<ITokenAccess> CredentialAsync(CancellationToken cancellationToken)
    {
        if (_credential is { } built)
        {
            return built;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_credential is null)
            {
                var json = options.Value.CredentialsJson;
                var credential = string.IsNullOrWhiteSpace(json)
                    ? await GoogleCredential.GetApplicationDefaultAsync(cancellationToken)
                    : CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential();
                _credential = credential.CreateScoped(Scope);
            }

            return _credential;
        }
        finally
        {
            _gate.Release();
        }
    }
}
