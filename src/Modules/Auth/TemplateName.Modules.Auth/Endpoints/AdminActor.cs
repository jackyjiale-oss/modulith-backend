using Microsoft.AspNetCore.Http;
using TemplateName.Application.Common.Identity;
using TemplateName.SharedKernel;
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
}
