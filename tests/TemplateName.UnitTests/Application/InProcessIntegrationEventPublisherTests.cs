using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Messaging;
using TemplateName.Infrastructure.Common.Messaging;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Application;

public sealed class InProcessIntegrationEventPublisherTests
{
    private static readonly TestIntegrationEvent Event =
        new(Guid.Parse("8d3c1a52-6f0e-4b8a-9c41-2f5e7d9a0b13"), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publishes_to_every_handler_in_its_own_scope()
    {
        var calls = new HandlerCalls();
        await using var provider = BuildProvider(calls, typeof(FirstRecordingHandler), typeof(SecondRecordingHandler));

        await Publisher(provider).PublishAsync(Event, Ct);

        calls.Entries.Select(entry => entry.Handler).ShouldBe([nameof(FirstRecordingHandler), nameof(SecondRecordingHandler)]);
        calls.Entries.ShouldAllBe(entry => entry.Event == Event);
        calls.Entries[0].Scope.ShouldNotBeSameAs(calls.Entries[1].Scope);

        // Each scope creates only the handler it runs.
        calls.Constructions.ShouldBe(2);

        // Each scope is disposed once its handler has run.
        calls.Entries.ShouldAllBe(entry => entry.Scope.IsDisposed);
    }

    [Fact]
    public async Task Runs_every_handler_even_when_one_throws_and_rethrows_the_failure()
    {
        var calls = new HandlerCalls();
        await using var provider = BuildProvider(calls, typeof(ThrowingHandler), typeof(SecondRecordingHandler));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Publisher(provider).PublishAsync(Event, Ct));

        exception.Message.ShouldBe(ThrowingHandler.Message);
        calls.Entries.Select(entry => entry.Handler).ShouldBe([nameof(ThrowingHandler), nameof(SecondRecordingHandler)]);
    }

    [Fact]
    public async Task Handler_that_cannot_be_created_does_not_stop_the_others()
    {
        var calls = new HandlerCalls();
        await using var provider = BuildProvider(calls, typeof(UnconstructibleHandler), typeof(FirstRecordingHandler));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => Publisher(provider).PublishAsync(Event, Ct));

        exception.Message.ShouldBe(UnconstructibleHandler.Message);
        calls.Entries.Select(entry => entry.Handler).ShouldBe([nameof(FirstRecordingHandler)]);
    }

    [Fact]
    public async Task Handlers_scanned_by_AddApplicationHandlers_run_once_each()
    {
        // Scanning the same assembly twice must not run a handler twice.
        await using var provider = new ServiceCollection()
            .AddApplicationHandlers(typeof(PingPublishedIntegrationEventHandler).Assembly)
            .AddApplicationHandlers(typeof(PingPublishedIntegrationEventHandler).Assembly)
            .AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>()
            .BuildServiceProvider();
        var pingPublished = new PingPublishedIntegrationEvent(Event.Id, Event.OccurredAt, []);

        await Publisher(provider).PublishAsync(pingPublished, Ct);

        pingPublished.Handlers.ShouldBe([nameof(PingPublishedIntegrationEventHandler)]);
    }

    [Fact]
    public async Task Two_failures_are_rethrown_as_aggregate()
    {
        var calls = new HandlerCalls();
        await using var provider = BuildProvider(calls, typeof(ThrowingHandler), typeof(FirstRecordingHandler), typeof(OtherThrowingHandler));

        var exception = await Should.ThrowAsync<AggregateException>(() => Publisher(provider).PublishAsync(Event, Ct));

        exception.InnerExceptions.Select(inner => inner.Message).ShouldBe([ThrowingHandler.Message, OtherThrowingHandler.Message]);
        calls.Entries.Select(entry => entry.Handler)
            .ShouldBe([nameof(ThrowingHandler), nameof(FirstRecordingHandler), nameof(OtherThrowingHandler)]);
    }

    [Fact]
    public async Task No_handler_is_a_no_op()
    {
        var calls = new HandlerCalls();
        await using var provider = BuildProvider(calls);

        await Publisher(provider).PublishAsync(Event, Ct);

        calls.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_is_not_wrapped()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var calls = new HandlerCalls { ToCancel = cancellation };
        await using var provider = BuildProvider(calls, typeof(ThrowingHandler), typeof(CancelingHandler), typeof(FirstRecordingHandler));

        // An earlier failure is not reported either: cancellation stops the publish at once.
        var exception = await Should.ThrowAsync<OperationCanceledException>(() => Publisher(provider).PublishAsync(Event, cancellation.Token));

        exception.ShouldNotBeOfType<AggregateException>();
        calls.Entries.Select(entry => entry.Handler).ShouldBe([nameof(ThrowingHandler), nameof(CancelingHandler)]);
    }

    private static ServiceProvider BuildProvider(HandlerCalls calls, params Type[] handlerTypes)
    {
        var services = new ServiceCollection()
            .AddSingleton(calls)
            .AddScoped<ScopeMarker>()
            .AddSingleton<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();

        foreach (var handlerType in handlerTypes)
        {
            services.AddIntegrationEventHandler(typeof(TestIntegrationEvent), handlerType);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static IIntegrationEventPublisher Publisher(IServiceProvider provider) => provider.GetRequiredService<IIntegrationEventPublisher>();

    private sealed record TestIntegrationEvent(Guid Id, DateTimeOffset OccurredAt) : IIntegrationEvent;

    /// <summary>One instance per DI scope; tells the scopes the handlers ran in apart.</summary>
    private sealed class ScopeMarker : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record HandlerCall(string Handler, TestIntegrationEvent Event, ScopeMarker Scope);

    private sealed class HandlerCalls
    {
        public List<HandlerCall> Entries { get; } = [];

        public CancellationTokenSource? ToCancel { get; init; }

        /// <summary>How many recording handlers were created.</summary>
        public int Constructions { get; set; }
    }

    private sealed class FirstRecordingHandler : IIntegrationEventHandler<TestIntegrationEvent>
    {
        private readonly HandlerCalls _calls;
        private readonly ScopeMarker _scope;

        public FirstRecordingHandler(HandlerCalls calls, ScopeMarker scope)
        {
            _calls = calls;
            _scope = scope;
            calls.Constructions++;
        }

        public Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            _calls.Entries.Add(new HandlerCall(nameof(FirstRecordingHandler), integrationEvent, _scope));
            return Task.CompletedTask;
        }
    }

    private sealed class SecondRecordingHandler : IIntegrationEventHandler<TestIntegrationEvent>
    {
        private readonly HandlerCalls _calls;
        private readonly ScopeMarker _scope;

        public SecondRecordingHandler(HandlerCalls calls, ScopeMarker scope)
        {
            _calls = calls;
            _scope = scope;
            calls.Constructions++;
        }

        public Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            _calls.Entries.Add(new HandlerCall(nameof(SecondRecordingHandler), integrationEvent, _scope));
            return Task.CompletedTask;
        }
    }

    /// <summary>A consumer whose construction fails, as with an invalid option or a throwing factory.</summary>
    private sealed class UnconstructibleHandler : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public const string Message = "Consumer could not be created.";

        public UnconstructibleHandler() => throw new InvalidOperationException(Message);

        public Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ThrowingHandler(HandlerCalls calls, ScopeMarker scope) : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public const string Message = "First consumer failed.";

        public Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            calls.Entries.Add(new HandlerCall(nameof(ThrowingHandler), integrationEvent, scope));
            throw new InvalidOperationException(Message);
        }
    }

    private sealed class OtherThrowingHandler(HandlerCalls calls, ScopeMarker scope) : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public const string Message = "Second consumer failed.";

        public async Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            calls.Entries.Add(new HandlerCall(nameof(OtherThrowingHandler), integrationEvent, scope));
            await Task.Yield();
            throw new InvalidOperationException(Message);
        }
    }

    private sealed class CancelingHandler(HandlerCalls calls, ScopeMarker scope) : IIntegrationEventHandler<TestIntegrationEvent>
    {
        public async Task HandleAsync(TestIntegrationEvent integrationEvent, CancellationToken cancellationToken)
        {
            calls.Entries.Add(new HandlerCall(nameof(CancelingHandler), integrationEvent, scope));
            await calls.ToCancel!.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
