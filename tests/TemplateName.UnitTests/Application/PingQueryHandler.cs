using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, string>
{
    public Task<Result<string>> HandleAsync(PingQuery query, CancellationToken cancellationToken)
        => Task.FromResult(Result.Success("pong"));
}
