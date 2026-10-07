using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

public sealed class HandlerRegistrationTests
{
    [Fact]
    public void Registration_resolves_internal_handler_wrapped_with_logging_outermost()
    {
        using var provider = BuildProvider(new ServiceCollection());
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PingCommand, string>>();

        handler.ShouldBeOfType<LoggingDecorator.CommandHandler<PingCommand, string>>();
    }

    [Fact]
    public async Task Command_with_response_is_logged_outside_and_validated_inside()
    {
        var logger = new RecordingLogger<LoggingDecorator.CommandHandler<PingCommand, string>>();
        await using var provider = BuildProvider(
            new ServiceCollection().AddSingleton<ILogger<LoggingDecorator.CommandHandler<PingCommand, string>>>(logger));
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PingCommand, string>>();

        var invalid = await handler.HandleAsync(new PingCommand(""), TestContext.Current.CancellationToken);
        var valid = await handler.HandleAsync(new PingCommand("ok"), TestContext.Current.CancellationToken);

        handler.ShouldBeOfType<LoggingDecorator.CommandHandler<PingCommand, string>>();
        invalid.Error.ShouldBeOfType<ValidationError>();
        valid.Value.ShouldBe("pong");
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("validation.failed"));
    }

    [Fact]
    public async Task Command_without_response_is_logged_outside_and_validated_inside()
    {
        var logger = new RecordingLogger<LoggingDecorator.CommandHandler<PingNoResponseCommand>>();
        await using var provider = BuildProvider(
            new ServiceCollection().AddSingleton<ILogger<LoggingDecorator.CommandHandler<PingNoResponseCommand>>>(logger));
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<PingNoResponseCommand>>();

        var invalid = await handler.HandleAsync(new PingNoResponseCommand(""), TestContext.Current.CancellationToken);
        var valid = await handler.HandleAsync(new PingNoResponseCommand("ok"), TestContext.Current.CancellationToken);

        handler.ShouldBeOfType<LoggingDecorator.CommandHandler<PingNoResponseCommand>>();
        invalid.Error.ShouldBeOfType<ValidationError>();
        valid.IsSuccess.ShouldBeTrue();
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("validation.failed"));
    }

    [Fact]
    public async Task Query_is_logged_outside_and_validated_inside()
    {
        var logger = new RecordingLogger<LoggingDecorator.QueryHandler<PingQuery, string>>();
        await using var provider = BuildProvider(
            new ServiceCollection().AddSingleton<ILogger<LoggingDecorator.QueryHandler<PingQuery, string>>>(logger));
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<PingQuery, string>>();

        var invalid = await handler.HandleAsync(new PingQuery(""), TestContext.Current.CancellationToken);
        var valid = await handler.HandleAsync(new PingQuery("ok"), TestContext.Current.CancellationToken);

        handler.ShouldBeOfType<LoggingDecorator.QueryHandler<PingQuery, string>>();
        invalid.Error.ShouldBeOfType<ValidationError>();
        valid.Value.ShouldBe("pong");
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("validation.failed"));
    }

    [Fact]
    public void Domain_event_handler_is_registered_without_decorators()
    {
        using var provider = BuildProvider(new ServiceCollection());
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<IDomainEventHandler<PingSentDomainEvent>>();

        handler.ShouldBeOfType<PingSentDomainEventHandler>();
    }

    [Fact]
    public void Validators_are_registered_including_internal_ones()
    {
        using var provider = BuildProvider(new ServiceCollection());
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IValidator<PingCommand>>().ShouldBeOfType<PingValidator>();
        scope.ServiceProvider.GetRequiredService<IValidator<PingQuery>>().ShouldBeOfType<PingQueryValidator>();
    }

    private static ServiceProvider BuildProvider(IServiceCollection services)
        => services
            .AddLogging()
            .AddApplicationHandlers(typeof(PingHandler).Assembly)
            .AddApplicationDecorators()
            .BuildServiceProvider();
}
