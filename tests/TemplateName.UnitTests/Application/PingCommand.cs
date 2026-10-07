using TemplateName.Application.Common.Messaging;

namespace TemplateName.UnitTests.Application;

internal sealed record PingCommand(string Reason) : ICommand<string>;
