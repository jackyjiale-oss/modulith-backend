namespace TemplateName.Application.Common.Messaging;

/// <summary>A request to read state without changing it.</summary>
/// <typeparam name="TResponse">The type of the success value.</typeparam>
public interface IQuery<TResponse>;
