using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.HubTickets;
using TemplateName.Modules.Notifications.Infrastructure.Hubs;

namespace TemplateName.UnitTests.Notifications;

public sealed class HubTicketAuthenticationHandlerTests
{
    private const string NegotiatePath = "/hubs/notifications/negotiate";
    private const string ConnectPath = "/hubs/notifications";

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly IHubTicketRepository _tickets = Substitute.For<IHubTicketRepository>();
    private readonly HubOptions _hubOptions = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/v1/notifications/inbox")]
    [InlineData("/hubs/notificationsx")]
    [InlineData("/hubs/other")]
    [InlineData("/")]
    public async Task Ticket_outside_the_hub_path_is_ignored(string path)
    {
        var (ticket, hash) = HubTicketTokens.Create();
        GivenValid(hash);
        var context = Request(path, query: ticket, header: ticket);

        var result = await AuthenticateAsync(context);

        result.None.ShouldBeTrue();
        await _tickets.DidNotReceiveWithAnyArgs().TryConsumeAsync(default!, default, Ct);
    }

    [Theory]
    [InlineData(NegotiatePath)]
    [InlineData("/HUBS/Notifications/negotiate")]
    public async Task Ticket_in_the_bearer_header_is_accepted_on_negotiate(string path)
    {
        var (ticket, hash) = HubTicketTokens.Create();
        GivenValid(hash);

        var result = await AuthenticateAsync(Request(path, header: ticket));

        result.Succeeded.ShouldBeTrue();
        result.Ticket!.AuthenticationScheme.ShouldBe(HubTicketAuthenticationOptions.SchemeName);
        var principal = result.Principal!;
        principal.Identity!.IsAuthenticated.ShouldBeTrue();
        principal.FindFirst("sub")!.Value.ShouldBe(UserId.ToString());
        principal.Claims.Count().ShouldBe(1);
        await _tickets.Received(1).TryConsumeAsync(Arg.Is<byte[]>(value => value.SequenceEqual(hash)), Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ticket_in_the_query_string_is_accepted_on_the_websocket_request()
    {
        var (ticket, hash) = HubTicketTokens.Create();
        GivenValid(hash);

        var result = await AuthenticateAsync(Request(ConnectPath, query: ticket));

        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindFirst("sub")!.Value.ShouldBe(UserId.ToString());
    }

    [Fact]
    public async Task Query_value_wins_over_the_header_and_only_one_ticket_is_consumed()
    {
        var (queryTicket, queryHash) = HubTicketTokens.Create();
        var (headerTicket, _) = HubTicketTokens.Create();
        GivenValid(queryHash);

        var result = await AuthenticateAsync(Request(ConnectPath, query: queryTicket, header: headerTicket));

        result.Succeeded.ShouldBeTrue();
        await _tickets.ReceivedWithAnyArgs(1).TryConsumeAsync(default!, default, Ct);
        await _tickets.Received(1).TryConsumeAsync(Arg.Is<byte[]>(value => value.SequenceEqual(queryHash)), Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Absent_ticket_is_no_result()
    {
        var withNothing = await AuthenticateAsync(Request(NegotiatePath));
        var withOtherScheme = await AuthenticateAsync(Request(NegotiatePath, authorization: "Basic dXNlcjpwYXNz"));
        var withEmptyQuery = await AuthenticateAsync(Request(ConnectPath, query: string.Empty));

        // A bearer header that is not ticket-shaped belongs to the JWT scheme, which shares the header on this path.
        var withJwtHeader = await AuthenticateAsync(Request(NegotiatePath, header: "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln"));

        withNothing.None.ShouldBeTrue();
        withOtherScheme.None.ShouldBeTrue();
        withEmptyQuery.None.ShouldBeTrue();
        withJwtHeader.None.ShouldBeTrue();
        await _tickets.DidNotReceiveWithAnyArgs().TryConsumeAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Used_or_expired_ticket_fails()
    {
        // The repository answers null for an unknown, used or expired ticket alike, and the handler cannot tell them apart either.
        var (ticket, _) = HubTicketTokens.Create();
        _tickets.TryConsumeAsync(default!, default, Ct).ReturnsForAnyArgs((Guid?)null);

        var fromHeader = await AuthenticateAsync(Request(NegotiatePath, header: ticket));
        var fromQuery = await AuthenticateAsync(Request(ConnectPath, query: ticket));

        fromHeader.Succeeded.ShouldBeFalse();
        fromHeader.None.ShouldBeFalse();
        fromHeader.Failure.ShouldNotBeNull().Message.ShouldBe(HubTicketAuthenticationHandler.FailureMessage);
        fromQuery.Failure.ShouldNotBeNull().Message.ShouldBe(HubTicketAuthenticationHandler.FailureMessage);
        fromHeader.Failure.Message.ShouldNotContain(ticket);
    }

    [Theory]
    [InlineData("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln")]
    [InlineData("too-short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("!!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Query_value_that_is_not_a_ticket_fails_without_a_lookup(string value)
    {
        // The query parameter belongs to tickets alone: a JWT there (where the JS client would put it) is refused, never read.
        var result = await AuthenticateAsync(Request(ConnectPath, query: value));

        result.Failure.ShouldNotBeNull().Message.ShouldBe(HubTicketAuthenticationHandler.FailureMessage);
        await _tickets.DidNotReceiveWithAnyArgs().TryConsumeAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Two_access_token_values_fail()
    {
        var (first, firstHash) = HubTicketTokens.Create();
        var (second, _) = HubTicketTokens.Create();
        GivenValid(firstHash);
        var context = Request(ConnectPath);
        context.Request.QueryString = new QueryString($"?access_token={first}&access_token={second}");

        var result = await AuthenticateAsync(context);

        result.Failure.ShouldNotBeNull();
        await _tickets.DidNotReceiveWithAnyArgs().TryConsumeAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Principal_expires_after_connection_lifetime()
    {
        _hubOptions.ConnectionLifetime = TimeSpan.FromMinutes(3);
        var (ticket, hash) = HubTicketTokens.Create();
        GivenValid(hash);

        var result = await AuthenticateAsync(Request(ConnectPath, query: ticket));

        result.Succeeded.ShouldBeTrue();
        result.Properties!.ExpiresUtc.ShouldBe(Now + TimeSpan.FromMinutes(3));
        result.Properties.IsPersistent.ShouldBeFalse();
    }

    [Fact]
    public void Ticket_is_32_random_bytes_in_base64url_and_its_hash_is_sha256_of_those_bytes()
    {
        var (ticket, hash) = HubTicketTokens.Create();
        var (other, _) = HubTicketTokens.Create();

        ticket.Length.ShouldBe(HubTicketTokens.TicketLength);
        ticket.ShouldNotContain("=");
        ticket.ShouldNotBe(other);
        var bytes = Base64Url.DecodeFromChars(ticket);
        bytes.Length.ShouldBe(32);
        hash.ShouldBe(SHA256.HashData(bytes));
        HubTicketTokens.TryHash(ticket, out var again).ShouldBeTrue();
        again.ShouldBe(hash);
    }

    private void GivenValid(byte[] hash)
        => _tickets.TryConsumeAsync(Arg.Is<byte[]>(value => value.SequenceEqual(hash)), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(UserId);

    private static DefaultHttpContext Request(string path, string? query = null, string? header = null, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (query is not null)
        {
            context.Request.QueryString = QueryString.Create("access_token", query);
        }

        if (header is not null)
        {
            context.Request.Headers.Authorization = $"Bearer {header}";
        }
        else if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private async Task<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        var options = Substitute.For<IOptionsMonitor<HubTicketAuthenticationOptions>>();
        options.Get(HubTicketAuthenticationOptions.SchemeName).Returns(new HubTicketAuthenticationOptions());
        var handler = new HubTicketAuthenticationHandler(
            options, NullLoggerFactory.Instance, UrlEncoder.Default, _tickets, new FakeTimeProvider(Now), Options.Create(_hubOptions));
        await handler.InitializeAsync(
            new AuthenticationScheme(HubTicketAuthenticationOptions.SchemeName, displayName: null, typeof(HubTicketAuthenticationHandler)), context);
        Ct.ThrowIfCancellationRequested();

        return await handler.AuthenticateAsync();
    }
}
