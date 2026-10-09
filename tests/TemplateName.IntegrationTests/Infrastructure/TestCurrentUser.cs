using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Identity;
using TemplateName.Web.Common.Identity;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The current user for integration tests. A test can force a user by setting <see cref="UserId"/> (persistence tests that run outside
/// a request do); while it is null, the real <see cref="HttpContextCurrentUser"/> answers, so request-scoped code sees the caller the
/// access token names, or anonymous.
/// </summary>
/// <remarks>
/// <see cref="HttpContextAccessor"/> keeps the request in a static async-local, so one instance sees the current request of whichever
/// host built by the factory is serving it.
/// </remarks>
public sealed class TestCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    private readonly HttpContextCurrentUser _requestUser = new(httpContextAccessor);
    private Guid? _forcedUserId;

    /// <summary>The forced user when set; otherwise the request's signed-in user. Setting null removes the override.</summary>
    public Guid? UserId
    {
        get => _forcedUserId ?? _requestUser.UserId;
        set => _forcedUserId = value;
    }

    /// <summary>The request's session; a forced user has none.</summary>
    public Guid? SessionId => _forcedUserId.HasValue ? null : _requestUser.SessionId;

    public bool IsAuthenticated => _forcedUserId.HasValue || _requestUser.IsAuthenticated;
}
