using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>Handles a query.</summary>
/// <typeparam name="TQuery">The query type.</typeparam>
/// <typeparam name="TResponse">The type of the success value.</typeparam>
public interface IQueryHandler<in TQuery, TResponse>
{
    Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
