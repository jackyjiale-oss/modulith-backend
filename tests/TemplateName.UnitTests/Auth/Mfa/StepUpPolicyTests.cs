using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.StepUp;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class StepUpPolicyTests
{
    // Whole seconds, like the auth_time claim the policy reads.
    private static readonly DateTimeOffset AuthTime = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly FakeTimeProvider _time = new(AuthTime);
    private readonly StepUpPolicy _sut;

    public StepUpPolicyTests()
    {
        _currentUser.AuthTime.Returns(AuthTime);
        _currentUser.AuthMethods.Returns(["pwd"]);
        _sut = new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions()), _time);
    }

    [Fact]
    public void Fresh_password_auth_satisfies_a_password_only_account()
    {
        _time.Advance(TimeSpan.FromMinutes(3));

        _sut.Check(hasConfirmedMfa: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Step_up_expires_exactly_after_max_age()
    {
        _time.SetUtcNow(AuthTime + MaxAge - TimeSpan.FromSeconds(1));
        _sut.Check(hasConfirmedMfa: false).IsSuccess.ShouldBeTrue();

        _time.SetUtcNow(AuthTime + MaxAge);
        _sut.Check(hasConfirmedMfa: false).Error.ShouldBe(MfaErrors.StepUpRequired);
    }

    [Theory]
    [InlineData(new[] { "pwd" }, false)]
    [InlineData(new[] { "otp" }, true)]
    [InlineData(new[] { "rcvr" }, true)]
    [InlineData(new[] { "pwd", "otp", "mfa" }, true)]
    [InlineData(new[] { "pwd", "rcvr", "mfa" }, true)]
    [InlineData(new[] { "mfa" }, false)]
    [InlineData(new string[0], false)]
    public void Mfa_account_needs_otp_or_rcvr_in_amr(string[] authMethods, bool satisfied)
    {
        _currentUser.AuthMethods.Returns(authMethods);

        var result = _sut.Check(hasConfirmedMfa: true);

        result.IsSuccess.ShouldBe(satisfied);
        if (!satisfied)
        {
            result.Error.ShouldBe(MfaErrors.StepUpRequired);
        }
    }

    [Fact]
    public void Missing_auth_time_fails()
    {
        _currentUser.AuthTime.Returns((DateTimeOffset?)null);
        _currentUser.AuthMethods.Returns(["otp"]);

        _sut.Check(hasConfirmedMfa: false).Error.ShouldBe(MfaErrors.StepUpRequired);
        _sut.Check(hasConfirmedMfa: true).Error.ShouldBe(MfaErrors.StepUpRequired);
    }

    [Fact]
    public void Max_age_follows_the_options()
    {
        var policy = new StepUpPolicy(_currentUser, Options.Create(new StepUpOptions { MaxAge = TimeSpan.FromMinutes(2) }), _time);

        _time.SetUtcNow(AuthTime + TimeSpan.FromSeconds(119));
        policy.Check(hasConfirmedMfa: false).IsSuccess.ShouldBeTrue();
        _time.SetUtcNow(AuthTime + TimeSpan.FromMinutes(2));
        policy.Check(hasConfirmedMfa: false).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Options_default_to_ten_minutes_under_auth_step_up()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        StepUpOptions.SectionName.ShouldBe("Auth:StepUp");
        services.GetRequiredService<IOptions<StepUpOptions>>().Value.MaxAge.ShouldBe(MaxAge);
    }

    [Theory]
    [InlineData("00:01:00", true)]
    [InlineData("01:00:00", true)]
    [InlineData("00:00:59", false)]
    [InlineData("01:00:01", false)]
    public void Max_age_from_one_minute_to_one_hour_starts(string maxAge, bool starts)
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Auth:StepUp:MaxAge"] = maxAge });

        if (starts)
        {
            Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        }
        else
        {
            var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());
            failure.OptionsType.ShouldBe(typeof(StepUpOptions));
            failure.Message.ShouldContain("MaxAge");
        }
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        AuthModule.AddMfa(services, configuration);
        return services.BuildServiceProvider();
    }
}
