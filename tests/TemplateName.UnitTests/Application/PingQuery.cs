using TemplateName.Application.Common.Messaging;

namespace TemplateName.UnitTests.Application;

internal sealed record PingQuery(string Reason) : IQuery<string>;
