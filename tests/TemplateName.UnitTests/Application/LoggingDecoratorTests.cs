using Microsoft.Extensions.Logging;
using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

public sealed class LoggingDecoratorTests
{
    private readonly ICommandHandler<PingCommand, string> _inner = Substitute.For<ICommandHandler<PingCommand, string>>();
    private readonly RecordingLogger<LoggingDecorator.CommandHandler<PingCommand, string>> _logger = new();

    [Fact]
    public async Task Success_logs_processing_and_completion_at_information()
    {
        _inner.HandleAsync(Arg.Any<PingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("pong"));
        var sut = new LoggingDecorator.CommandHandler<PingCommand, string>(_inner, _logger);

        var result = await sut.HandleAsync(new PingCommand("ok"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("pong");
        _logger.Entries.Count.ShouldBe(2);
        _logger.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Information);
        _logger.Entries[0].Message.ShouldContain("PingCommand");
        _logger.Entries[1].Message.ShouldContain("PingCommand");
    }

    [Fact]
    public async Task Failure_logs_a_warning_with_the_error_code()
    {
        _inner.HandleAsync(Arg.Any<PingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>(Error.Conflict("ping.already_sent", "x")));
        var sut = new LoggingDecorator.CommandHandler<PingCommand, string>(_inner, _logger);

        var result = await sut.HandleAsync(new PingCommand("ok"), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        var warning = _logger.Entries.Single(entry => entry.Level == LogLevel.Warning);
        warning.Message.ShouldContain("ping.already_sent");
    }

    [Fact]
    public async Task Exception_propagates_without_being_logged_as_a_failure()
    {
        _inner.HandleAsync(Arg.Any<PingCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<string>>>(_ => throw new InvalidOperationException("boom"));
        var sut = new LoggingDecorator.CommandHandler<PingCommand, string>(_inner, _logger);

        await Should.ThrowAsync<InvalidOperationException>(
            () => sut.HandleAsync(new PingCommand("ok"), TestContext.Current.CancellationToken));

        _logger.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
    }
}
