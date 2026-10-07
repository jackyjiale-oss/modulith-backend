using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;

namespace TemplateName.Infrastructure.Common.Idempotency;

/// <summary>
/// Handles <c>Idempotency-Key</c> on endpoints marked with <see cref="IdempotentEndpointMetadata"/>. The first request with a key
/// inserts an in-progress row; the primary key (<c>Scope</c>, <c>Key</c>) makes concurrent duplicates fail that insert, so the
/// endpoint runs once and the others get 409 <c>idempotency.in_progress</c>. A response below 500 is stored and replayed to retries
/// with the same body (<c>Idempotency-Replayed: true</c>); a 5xx or an exception deletes the row so the client can retry.
/// </summary>
/// <remarks>
/// <para>
/// The in-progress row is a lease: it expires <see cref="IdempotencyOptions.InProgressTimeout"/> after the request started, and it
/// carries the request's owner token (<see cref="IdempotencyRecord.LockId"/>). Once the lease has ended, the next request with the
/// key reclaims the row (the same compare-and-delete that frees any expired row) and runs the endpoint, so a key whose request died
/// is not blocked for the whole time to live. Storing the response (which extends the expiry to <see cref="IdempotencyOptions.TimeToLive"/>)
/// and deleting the row both match the owner token, so a request that outlived its lease cannot overwrite or release the row of the
/// request that took the key over.
/// </para>
/// <para>
/// The response is buffered and stored before it is sent, so a client that has the response can retry straight away and gets the
/// replay. If storing it fails, the response is still sent and the row stays in progress until its lease ends: retries get 409 until
/// then, and run the endpoint again after it. Clean-ups after the endpoint ran ignore <see cref="HttpContext.RequestAborted"/>, so an
/// aborted request still stores or deletes its row.
/// </para>
/// </remarks>
internal sealed partial class IdempotencyMiddleware(
    RequestDelegate next,
    TimeProvider timeProvider,
    IOptions<IdempotencyOptions> options,
    IProblemDetailsService problemDetailsService,
    ILogger<IdempotencyMiddleware> logger)
{
    internal const string KeyHeaderName = "Idempotency-Key";
    internal const string ReplayedHeaderName = "Idempotency-Replayed";
    internal const string AnonymousScope = "anonymous";

    private const int HashBufferSize = 16 * 1024;

    // Primary key or unique constraint violation, and duplicate key in a unique index.
    private const int SqlUniqueConstraintViolation = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    // English defaults; CustomizeProblemDetails replaces them with the InfrastructureErrorMessages entries for the caller's language.
    private const string InvalidKeyCode = "idempotency.invalid_key";
    private const string InvalidKeyDetail = "The Idempotency-Key header must be a single value of 1 to 100 characters.";
    private const string InProgressCode = "idempotency.in_progress";
    private const string InProgressDetail = "A request with this Idempotency-Key is still being processed. Retry later.";
    private const string KeyReusedCode = "idempotency.key_reused";
    private const string KeyReusedDetail = "This Idempotency-Key was already used for a different request.";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentEndpointMetadata>() is null
            || !context.Request.Headers.TryGetValue(KeyHeaderName, out var keyValues))
        {
            await next(context);
            return;
        }

        var key = keyValues.Count == 1 ? keyValues[0] : null;
        if (string.IsNullOrEmpty(key) || key.Length > IdempotencyRecord.MaxKeyLength)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, InvalidKeyCode, InvalidKeyDetail);
            return;
        }

        var cancellationToken = context.RequestAborted;
        var db = context.RequestServices.GetRequiredService<PlatformDbContext>();
        var scope = context.RequestServices.GetRequiredService<ICurrentUser>().UserId?.ToString() ?? AnonymousScope;
        var requestHash = await ComputeRequestHashAsync(context.Request, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var existing = await db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(record => record.Scope == scope && record.Key == key, cancellationToken);

        if (existing is not null && existing.ExpiresAt <= now)
        {
            // Only while it is still expired, so a fresh row a concurrent request has just inserted survives.
            await db.IdempotencyKeys
                .Where(record => record.Scope == scope && record.Key == key && record.ExpiresAt <= now)
                .ExecuteDeleteAsync(cancellationToken);
            existing = null;
        }

        if (existing is not null)
        {
            await RespondWithExistingAsync(context, existing, requestHash, cancellationToken);
            return;
        }

        var lockId = Guid.NewGuid();
        var inProgress = new IdempotencyRecord
        {
            Scope = scope,
            Key = key,
            RequestHash = requestHash,
            CreatedAt = now,
            ExpiresAt = now + options.Value.InProgressTimeout,
            LockId = lockId,
        };

        if (!await TryInsertAsync(db, inProgress, cancellationToken))
        {
            // Either a concurrent request inserted first, or this request's own insert committed and a transient error made the
            // retry strategy insert again. The owner token tells the two apart.
            var winner = await db.IdempotencyKeys.AsNoTracking()
                .SingleOrDefaultAsync(record => record.Scope == scope && record.Key == key, cancellationToken);

            if (winner is null)
            {
                // The other request released the key in between; the client may retry.
                await WriteProblemAsync(context, StatusCodes.Status409Conflict, InProgressCode, InProgressDetail);
                return;
            }

            if (winner.LockId != lockId)
            {
                await RespondWithExistingAsync(context, winner, requestHash, cancellationToken);
                return;
            }
        }

        await RunAndStoreAsync(context, db, scope, key, lockId);
    }

    private static async Task<byte[]> ComputeRequestHashAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.PathBase}{request.Path}\n"));

        var buffer = new byte[HashBufferSize];
        int bytesRead;
        while ((bytesRead = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, bytesRead);
        }

        // Rewind, so the endpoint reads the body from the start.
        request.Body.Position = 0;
        return hash.GetHashAndReset();
    }

    private static async Task<bool> TryInsertAsync(PlatformDbContext db, IdempotencyRecord record, CancellationToken cancellationToken)
    {
        db.IdempotencyKeys.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: SqlUniqueConstraintViolation or SqlUniqueIndexViolation })
        {
            // A row with the same key exists; the caller reads it to find out whose it is.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task RespondWithExistingAsync(
        HttpContext context,
        IdempotencyRecord existing,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        if (!existing.RequestHash.AsSpan().SequenceEqual(requestHash))
        {
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity, KeyReusedCode, KeyReusedDetail);
            return;
        }

        if (existing.StatusCode is not { } statusCode)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, InProgressCode, InProgressDetail);
            return;
        }

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentType = existing.ContentType;
        if (existing.Location is not null)
        {
            response.Headers.Location = existing.Location;
        }

        response.Headers[ReplayedHeaderName] = "true";

        if (existing.ResponseBody is { Length: > 0 } body)
        {
            response.ContentLength = body.Length;
            await response.Body.WriteAsync(body, cancellationToken);
        }
    }

    private async Task RunAndStoreAsync(HttpContext context, PlatformDbContext db, string scope, string key, Guid lockId)
    {
        // Buffer the response: it must be stored before the client sees it. Headers stay on the response, and the response starts
        // (running OnStarting callbacks) only when the buffer is copied to the real body.
        var originalBody = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
        await using var buffer = new MemoryStream();
        var bufferedBody = new StreamResponseBodyFeature(buffer, originalBody);
        context.Features.Set<IHttpResponseBodyFeature>(bufferedBody);

        try
        {
            await next(context);
            await bufferedBody.CompleteAsync();
        }
        catch
        {
            context.Features.Set(originalBody);
            await TryDeleteAsync(db, scope, key, lockId);
            throw;
        }

        context.Features.Set(originalBody);

        var response = context.Response;
        if (response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            await TryDeleteAsync(db, scope, key, lockId);
        }
        else
        {
            await TryStoreAsync(db, scope, key, lockId, response, buffer.ToArray());
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(response.Body, context.RequestAborted);
    }

    private async Task TryStoreAsync(PlatformDbContext db, string scope, string key, Guid lockId, HttpResponse response, byte[] body)
    {
        var statusCode = response.StatusCode;
        var contentType = response.ContentType;
        string? location = response.Headers.Location;
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime + options.Value.TimeToLive;

        try
        {
            var stored = await db.IdempotencyKeys
                .Where(record => record.Scope == scope && record.Key == key && record.StatusCode == null && record.LockId == lockId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(record => record.StatusCode, statusCode)
                        .SetProperty(record => record.ContentType, contentType)
                        .SetProperty(record => record.Location, location)
                        .SetProperty(record => record.ResponseBody, body)
                        .SetProperty(record => record.ExpiresAt, expiresAt),
                    CancellationToken.None);

            if (stored == 0)
            {
                LogLeaseLost(logger, key);
            }
        }
        catch (Exception exception)
        {
            LogStoreFailed(logger, exception, key);
        }
    }

    private async Task TryDeleteAsync(PlatformDbContext db, string scope, string key, Guid lockId)
    {
        try
        {
            var deleted = await db.IdempotencyKeys
                .Where(record => record.Scope == scope && record.Key == key && record.StatusCode == null && record.LockId == lockId)
                .ExecuteDeleteAsync(CancellationToken.None);

            if (deleted == 0)
            {
                LogLeaseLost(logger, key);
            }
        }
        catch (Exception exception)
        {
            LogDeleteFailed(logger, exception, key);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = status,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not store the response for idempotency key {IdempotencyKey}; it stays in progress until its lease ends")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not release idempotency key {IdempotencyKey}; it stays in progress until its lease ends")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idempotency key {IdempotencyKey} was taken over by another request after this request's lease ended; its outcome was not recorded")]
    private static partial void LogLeaseLost(ILogger logger, string idempotencyKey);
}
