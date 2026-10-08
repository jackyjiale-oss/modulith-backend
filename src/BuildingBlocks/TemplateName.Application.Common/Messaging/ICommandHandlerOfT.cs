using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>Handles a command that returns a value.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResponse">The type of the success value.</typeparam>
public interface ICommandHandler<in TCommand, TResponse>
{
    Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
