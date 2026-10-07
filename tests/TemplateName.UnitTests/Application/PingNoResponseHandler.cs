using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

internal sealed class PingNoResponseHandler : ICommandHandler<PingNoResponseCommand>
{
    public Task<Result> HandleAsync(PingNoResponseCommand command, CancellationToken cancellationToken)
        => Task.FromResult(Result.Success());
}
