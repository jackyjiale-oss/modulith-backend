using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.HubTickets;
using TemplateName.Modules.Notifications.Application.HubTickets.Issue;
using TemplateName.Modules.Notifications.Domain.HubTickets;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Notifications;

public sealed class IssueHubTicketCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IHubTicketRepository _tickets = Substitute.For<IHubTicketRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly HubOptions _options = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issue_stores_only_the_hash_of_the_ticket()
    {
        _currentUser.UserId.Returns(UserId);
        HubTicket? stored = null;
        _tickets.Add(Arg.Do<HubTicket>(ticket => stored = ticket));

        var result = await Handler().HandleAsync(new IssueHubTicketCommand(), Ct);

        result.IsSuccess.ShouldBeTrue();
        var ticket = result.Value.Ticket;
        stored.ShouldNotBeNull();
        stored.UserId.ShouldBe(UserId);
        stored.TokenHash.ShouldBe(SHA256.HashData(Base64Url.DecodeFromChars(ticket)));

        // Neither the ticket's bytes nor its text are what is stored.
        stored.TokenHash.ShouldNotBe(Base64Url.DecodeFromChars(ticket));
        stored.TokenHash.ShouldNotBe(Encoding.ASCII.GetBytes(ticket));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Issue_uses_the_configured_ticket_lifetime()
    {
        _currentUser.UserId.Returns(UserId);
        _options.TicketLifetime = TimeSpan.FromSeconds(10);
        HubTicket? stored = null;
        _tickets.Add(Arg.Do<HubTicket>(ticket => stored = ticket));

        var result = await Handler().HandleAsync(new IssueHubTicketCommand(), Ct);

        result.Value.ExpiresAt.ShouldBe(Now.AddSeconds(10));
        stored.ShouldNotBeNull();
        stored.CreatedAt.ShouldBe(Now);
        stored.ExpiresAt.ShouldBe(Now.AddSeconds(10));
    }

    [Fact]
    public async Task Every_ticket_is_new()
    {
        _currentUser.UserId.Returns(UserId);

        var first = await Handler().HandleAsync(new IssueHubTicketCommand(), Ct);
        var second = await Handler().HandleAsync(new IssueHubTicketCommand(), Ct);

        first.Value.Ticket.ShouldNotBe(second.Value.Ticket);
    }

    [Fact]
    public async Task Caller_without_a_user_id_gets_no_ticket()
    {
        _currentUser.UserId.Returns((Guid?)null);

        var result = await Handler().HandleAsync(new IssueHubTicketCommand(), Ct);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Unauthorized);
        _tickets.DidNotReceiveWithAnyArgs().Add(default!);
        await _unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(Ct);
    }

    private IssueHubTicketCommandHandler Handler()
        => new(_tickets, _unitOfWork, _currentUser, new FakeTimeProvider(Now), Options.Create(_options));
}
