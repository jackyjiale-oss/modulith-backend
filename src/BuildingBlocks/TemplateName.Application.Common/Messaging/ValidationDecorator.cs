using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>Handler decorators that run the FluentValidation validators before the inner handler.</summary>
internal static class ValidationDecorator
{
    internal sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        IEnumerable<IValidator<TCommand>> validators)
        : ICommandHandler<TCommand>
    {
        public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken)
        {
            var error = await ValidateAsync(validators, command, cancellationToken);

            return error is null
                ? await inner.HandleAsync(command, cancellationToken)
                : Result.Failure(error);
        }
    }

    internal sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        IEnumerable<IValidator<TCommand>> validators)
        : ICommandHandler<TCommand, TResponse>
    {
        public async Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken)
        {
            var error = await ValidateAsync(validators, command, cancellationToken);

            return error is null
                ? await inner.HandleAsync(command, cancellationToken)
                : Result.Failure<TResponse>(error);
        }
    }

    internal sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        IEnumerable<IValidator<TQuery>> validators)
        : IQueryHandler<TQuery, TResponse>
    {
        public async Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken)
        {
            var error = await ValidateAsync(validators, query, cancellationToken);

            return error is null
                ? await inner.HandleAsync(query, cancellationToken)
                : Result.Failure<TResponse>(error);
        }
    }

    private static async Task<ValidationError?> ValidateAsync<T>(
        IEnumerable<IValidator<T>> validators,
        T instance,
        CancellationToken cancellationToken)
    {
        var context = new ValidationContext<T>(instance);
        var failures = new List<ValidationFailure>();

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var errors = failures
            .GroupBy(failure => ToCamelCasePath(failure.PropertyName))
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());

        return new ValidationError(errors);
    }

    // "Items[0].Name" -> "items[0].name": camel-case every dotted segment, keep any [n] index suffix as is.
    private static string ToCamelCasePath(string propertyPath)
        => string.Join('.', propertyPath.Split('.').Select(ToCamelCaseSegment));

    private static string ToCamelCaseSegment(string segment)
    {
        var indexStart = segment.IndexOf('[', StringComparison.Ordinal);

        return indexStart < 0
            ? JsonNamingPolicy.CamelCase.ConvertName(segment)
            : JsonNamingPolicy.CamelCase.ConvertName(segment[..indexStart]) + segment[indexStart..];
    }
}
