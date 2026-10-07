using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

internal sealed class PingHandler : ICommandHandler<PingCommand, string>
{
    public Task<Result<string>> HandleAsync(PingCommand command, CancellationToken cancellationToken)
        => Task.FromResult(Result.Success("pong"));
}
