using NSubstitute;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

public sealed class ValidationDecoratorTests
{
    [Fact]
    public async Task Invalid_command_returns_validation_error_without_calling_inner()
    {
        var inner = Substitute.For<ICommandHandler<PingCommand, string>>();
        var sut = new ValidationDecorator.CommandHandler<PingCommand, string>(inner, [new PingValidator()]);

        var result = await sut.HandleAsync(new PingCommand(""), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("reason");
        await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_command_calls_inner()
    {
        var inner = Substitute.For<ICommandHandler<PingCommand, string>>();
        inner.HandleAsync(Arg.Any<PingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("pong"));
        var sut = new ValidationDecorator.CommandHandler<PingCommand, string>(inner, [new PingValidator()]);

        var result = await sut.HandleAsync(new PingCommand("ok"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("pong");
    }

    [Fact]
    public async Task Command_without_validators_calls_inner()
    {
        var inner = Substitute.For<ICommandHandler<PingCommand, string>>();
        inner.HandleAsync(Arg.Any<PingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("pong"));
        var sut = new ValidationDecorator.CommandHandler<PingCommand, string>(inner, []);

        var result = await sut.HandleAsync(new PingCommand(""), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("pong");
    }
}
