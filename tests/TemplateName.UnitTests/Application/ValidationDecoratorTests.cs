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

    [Fact]
    public async Task Nested_property_keys_are_camel_cased_per_segment()
    {
        var inner = Substitute.For<ICommandHandler<OrderCommand, string>>();
        var sut = new ValidationDecorator.CommandHandler<OrderCommand, string>(inner, [new OrderCommandValidator()]);
        var command = new OrderCommand(new OrderCommand.AddressLine(""), [new OrderCommand.ItemLine("ok")]);

        var result = await sut.HandleAsync(command, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldBe(["address.street"]);
    }

    [Fact]
    public async Task Indexed_collection_keys_keep_the_index_and_camel_case_the_rest()
    {
        var inner = Substitute.For<ICommandHandler<OrderCommand, string>>();
        var sut = new ValidationDecorator.CommandHandler<OrderCommand, string>(inner, [new OrderCommandValidator()]);
        var command = new OrderCommand(
            new OrderCommand.AddressLine("Main Street"),
            [new OrderCommand.ItemLine("ok"), new OrderCommand.ItemLine("")]);

        var result = await sut.HandleAsync(command, TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldBe(["items[1].name"]);
    }

    [Fact]
    public async Task Invalid_command_without_response_returns_validation_error_without_calling_inner()
    {
        var inner = Substitute.For<ICommandHandler<PingNoResponseCommand>>();
        var sut = new ValidationDecorator.CommandHandler<PingNoResponseCommand>(inner, [new PingNoResponseValidator()]);

        var result = await sut.HandleAsync(new PingNoResponseCommand(""), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("reason");
        await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_command_without_response_calls_inner()
    {
        var inner = Substitute.For<ICommandHandler<PingNoResponseCommand>>();
        inner.HandleAsync(Arg.Any<PingNoResponseCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var sut = new ValidationDecorator.CommandHandler<PingNoResponseCommand>(inner, [new PingNoResponseValidator()]);

        var result = await sut.HandleAsync(new PingNoResponseCommand("ok"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await inner.ReceivedWithAnyArgs(1).HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Invalid_query_returns_validation_error_without_calling_inner()
    {
        var inner = Substitute.For<IQueryHandler<PingQuery, string>>();
        var sut = new ValidationDecorator.QueryHandler<PingQuery, string>(inner, [new PingQueryValidator()]);

        var result = await sut.HandleAsync(new PingQuery(""), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>().Errors.Keys.ShouldContain("reason");
        await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_query_calls_inner()
    {
        var inner = Substitute.For<IQueryHandler<PingQuery, string>>();
        inner.HandleAsync(Arg.Any<PingQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success("pong"));
        var sut = new ValidationDecorator.QueryHandler<PingQuery, string>(inner, [new PingQueryValidator()]);

        var result = await sut.HandleAsync(new PingQuery("ok"), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("pong");
    }
}
