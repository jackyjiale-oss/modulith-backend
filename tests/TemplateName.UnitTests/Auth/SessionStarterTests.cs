using Microsoft.Extensions.Options;
using NSubstitute;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.UnitTests.Auth;

public sealed class SessionStarterTests
{
    private const string UserAgent = "UnitTests/1.0";
    private const string IpAddress = "203.0.113.7";

    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ISecureTokenService _tokenService = Substitute.For<ISecureTokenService>();
    private readonly IAccessTokenIssuer _accessTokenIssuer = Substitute.For<IAccessTokenIssuer>();
    private readonly IClientContext _clientContext = Substitute.For<IClientContext>();
    private readonly SessionStarter _sut;

    public SessionStarterTests()
    {
        _tokenService.Generate().Returns(new GeneratedToken("refresh-token-value-of-forty-three-chars-xx", [.. Enumerable.Range(1, 32).Select(value => (byte)value)]));
        _accessTokenIssuer.Issue(Arg.Any<AccessTokenRequest>()).Returns(new AccessToken("header.payload.signature", Now.AddMinutes(10)));
        _clientContext.UserAgent.Returns(UserAgent);
        _clientContext.IpAddress.Returns(IpAddress);
        _sut = new SessionStarter(_sessions, _tokenService, _accessTokenIssuer, _clientContext, Options.Create(new RefreshTokenOptions()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(nameof(DeviceFamiliarity.NoStoredSession), false, false)]
    [InlineData(nameof(DeviceFamiliarity.Known), false, false)]
    [InlineData(nameof(DeviceFamiliarity.New), false, true)]
    [InlineData(nameof(DeviceFamiliarity.New), true, false)]
    [InlineData(nameof(DeviceFamiliarity.Known), true, false)]
    [InlineData(nameof(DeviceFamiliarity.NoStoredSession), true, false)]
    public async Task SessionStarter_notes_a_new_device_only_for_New_and_never_via_a_trusted_device(
        string familiarityName,
        bool viaTrustedDevice,
        bool expectsNewDevice)
    {
        var user = GivenUser();
        _sessions.GetDeviceFamiliarityAsync(user.Id, "Pixel 9", UserAgent, Arg.Any<CancellationToken>()).Returns(Enum.Parse<DeviceFamiliarity>(familiarityName));

        var started = await _sut.StartAsync(user, "pwd", "Pixel 9", viaTrustedDevice, Now, Ct);

        if (expectsNewDevice)
        {
            started.Session.DomainEvents.ShouldHaveSingleItem()
                .ShouldBe(new NewDeviceSignedInDomainEvent(user.Id, started.Session.Id, IpAddress));
        }
        else
        {
            started.Session.DomainEvents.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_user_who_signed_in_before_but_has_no_stored_session_left_is_on_a_new_device()
    {
        // As after AuthSessionsCleanup on a dormant account: an earlier sign-in, but every session of it deleted.
        var user = GivenUser();
        user.RecordSuccessfulSignIn(Now.AddDays(-200));
        _sessions.GetDeviceFamiliarityAsync(user.Id, "Pixel 9", UserAgent, Ct).Returns(DeviceFamiliarity.NoStoredSession);

        var started = await _sut.StartAsync(user, "pwd", "Pixel 9", viaTrustedDevice: false, Now, Ct);

        started.Session.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new NewDeviceSignedInDomainEvent(user.Id, started.Session.Id, IpAddress));
        user.LastLoginAt.ShouldBe(Now);
    }

    [Fact]
    public async Task A_user_who_signed_in_before_through_a_trusted_device_with_no_stored_session_is_not_on_a_new_device()
    {
        var user = GivenUser();
        user.RecordSuccessfulSignIn(Now.AddDays(-200));

        var started = await _sut.StartAsync(user, "pwd", "Pixel 9", viaTrustedDevice: true, Now, Ct);

        started.Session.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_trusted_device_sign_in_does_not_even_ask_the_repository()
    {
        var user = GivenUser();

        await _sut.StartAsync(user, "pwd", "Pixel 9", viaTrustedDevice: true, Now, Ct);

        await _sessions.DidNotReceiveWithAnyArgs().GetDeviceFamiliarityAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task The_repository_is_asked_before_the_new_session_is_added()
    {
        var user = GivenUser();
        _sessions.GetDeviceFamiliarityAsync(user.Id, "Pixel 9", UserAgent, Ct).Returns(DeviceFamiliarity.Known);

        await _sut.StartAsync(user, "pwd", "Pixel 9", viaTrustedDevice: false, Now, Ct);

        Received.InOrder(() =>
        {
            _sessions.GetDeviceFamiliarityAsync(user.Id, "Pixel 9", UserAgent, Ct);
            _sessions.Add(Arg.Any<UserSession>());
        });
    }

    [Fact]
    public async Task A_device_name_longer_than_its_column_is_compared_as_stored()
    {
        var user = GivenUser();
        var longName = new string('d', UserSession.MaxDeviceNameLength + 50);
        var stored = new string('d', UserSession.MaxDeviceNameLength);
        _sessions.GetDeviceFamiliarityAsync(user.Id, stored, UserAgent, Ct).Returns(DeviceFamiliarity.Known);

        var started = await _sut.StartAsync(user, "pwd", longName, viaTrustedDevice: false, Now, Ct);

        started.Session.DomainEvents.ShouldBeEmpty();
        await _sessions.Received(1).GetDeviceFamiliarityAsync(user.Id, stored, UserAgent, Ct);
    }

    private static User GivenUser()
    {
        var user = User.Register("alice@example.com", "Alice", "en", "stored-hash", Now.AddDays(-1)).Value;
        user.ConfirmEmail(Now.AddDays(-1));
        user.ClearDomainEvents();
        return user;
    }
}
