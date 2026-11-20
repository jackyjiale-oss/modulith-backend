using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Concurrency;
using TemplateName.Application.Common.Identity;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Concurrency;
using TemplateName.Web.Common.Results;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>What the administration routes share: every action names the acting administrator.</summary>
internal static class AdminActor
{
    /// <summary>
    /// Runs <paramref name="send"/> for the caller. The permission policy only lets a signed-in user with an id through; without one,
    /// fail closed with the body-less 401 (<c>UseStatusCodePages</c> turns it into the <c>http.401</c> problem) rather than act as
    /// nobody, every SuperAdmin rule included. On success answer with <paramref name="success"/>; otherwise with the error's problem.
    /// </summary>
    public static async Task<IResult> RunAsync(ICurrentUser currentUser, Func<Guid, Task<Result>> send, IResult success)
    {
        if (currentUser.UserId is not { } actorId)
        {
            return TypedResults.Unauthorized();
        }

        var result = await send(actorId);

        return result.IsSuccess ? success : result.ToProblem();
    }

    /// <summary>
    /// As <see cref="RunAsync(ICurrentUser, Func{Guid, Task{Result}}, IResult)"/> for an update that takes <c>If-Match</c>: on success
    /// 204 with the resource's new version as <c>ETag</c> (ADR 0038).
    /// </summary>
    public static async Task<IResult> RunAsync(ICurrentUser currentUser, Func<Guid, Task<Result<EntityTag>>> send)
    {
        if (currentUser.UserId is not { } actorId)
        {
            return TypedResults.Unauthorized();
        }

        var result = await send(actorId);

        return result.IsSuccess ? TypedResults.NoContent().WithEntityTag(result.Value) : result.ToProblem();
    }
}
