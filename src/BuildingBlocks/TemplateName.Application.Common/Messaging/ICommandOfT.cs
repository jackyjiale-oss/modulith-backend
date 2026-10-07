namespace TemplateName.Application.Common.Messaging;

/// <summary>A request to change state that returns a value on success.</summary>
/// <typeparam name="TResponse">The type of the success value.</typeparam>
public interface ICommand<TResponse> : IBaseCommand;
