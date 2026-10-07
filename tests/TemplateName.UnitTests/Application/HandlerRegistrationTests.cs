using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

public sealed class HandlerRegistrationTests
{
    [Fact]
    public void Registration_resolves_internal_handler_wrapped_with_logging_outermost()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddApplicationHandlers(typeof(PingHandler).Assembly)
            .AddApplicationDecorators()
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PingCommand, string>>();

        handler.ShouldBeOfType<LoggingDecorator.CommandHandler<PingCommand, string>>();
    }

    [Fact]
    public async Task Registered_pipeline_validates_before_reaching_the_handler()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddApplicationHandlers(typeof(PingHandler).Assembly)
            .AddApplicationDecorators()
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PingCommand, string>>();

        var invalid = await handler.HandleAsync(new PingCommand(""), TestContext.Current.CancellationToken);
        var valid = await handler.HandleAsync(new PingCommand("ok"), TestContext.Current.CancellationToken);

        invalid.Error.ShouldBeOfType<ValidationError>();
        valid.Value.ShouldBe("pong");
    }
}
