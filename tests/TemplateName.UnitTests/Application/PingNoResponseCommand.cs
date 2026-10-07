using TemplateName.Application.Common.Messaging;

namespace TemplateName.UnitTests.Application;

internal sealed record PingNoResponseCommand(string Reason) : ICommand;
