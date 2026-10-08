using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>Handles a command that returns no value.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
public interface ICommandHandler<in TCommand>
{
    Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
