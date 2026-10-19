using System.Text;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class TotpTests
{
    /// <summary>The SHA-1 seed of RFC 6238 Appendix B: the ASCII bytes of <c>12345678901234567890</c>.</summary>
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>A fixed instant in the middle of a time step, so the neighbouring steps are deterministic.</summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_760_000_015);

    [Theory]
    [InlineData(59L, "94287082", "287082")]
    [InlineData(1_111_111_109L, "07081804", "081804")]
    [InlineData(1_111_111_111L, "14050471", "050471")]
    [InlineData(1_234_567_890L, "89005924", "005924")]
    [InlineData(2_000_000_000L, "69279037", "279037")]
    [InlineData(20_000_000_000L, "65353130", "353130")]
    public void ComputeCode_matches_the_rfc_6238_sha1_vectors(long unixSeconds, string eightDigitCode, string sixDigitCode)
    {
        var timeStep = Totp.TimeStepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Totp.ComputeCode(RfcSecret, timeStep, digits: 8).ShouldBe(eightDigitCode);
        Totp.ComputeCode(RfcSecret, timeStep).ShouldBe(sixDigitCode);
    }

    [Fact]
    public void Parameters_are_the_ones_every_authenticator_app_supports()
    {
        Totp.Digits.ShouldBe(6);
        Totp.StepSeconds.ShouldBe(30);
        Totp.SecretLength.ShouldBe(20);
    }

    [Fact]
    public void TimeStepAt_changes_exactly_on_the_thirty_second_boundary()
    {
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeSeconds(0)).ShouldBe(0);
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeMilliseconds(29_999)).ShouldBe(0);
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeSeconds(30)).ShouldBe(1);
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeSeconds(59)).ShouldBe(1);
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeSeconds(60)).ShouldBe(2);
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeMilliseconds(89_999)).ShouldBe(2);
        Totp.TimeStepAt(DateTimeOffset.FromUnixTimeSeconds(90)).ShouldBe(3);
    }

    [Fact]
    public void TimeStepAt_ignores_the_offset_of_the_instant()
    {
        var utc = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        var kualaLumpur = utc.ToOffset(TimeSpan.FromHours(8));

        Totp.TimeStepAt(kualaLumpur).ShouldBe(Totp.TimeStepAt(utc));
    }

    [Fact]
    public void FindMatchingStep_accepts_one_step_either_side_and_refuses_two()
    {
        var current = Totp.TimeStepAt(Now);

        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current - 1), Now, lastUsedTimeStep: null).ShouldBe(current - 1);
        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current), Now, lastUsedTimeStep: null).ShouldBe(current);
        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current + 1), Now, lastUsedTimeStep: null).ShouldBe(current + 1);

        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current - 2), Now, lastUsedTimeStep: null).ShouldBeNull();
        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current + 2), Now, lastUsedTimeStep: null).ShouldBeNull();
    }

    [Fact]
    public void FindMatchingStep_refuses_steps_at_or_below_the_last_used_one()
    {
        var current = Totp.TimeStepAt(Now);

        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current), Now, lastUsedTimeStep: current).ShouldBeNull();
        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current - 1), Now, lastUsedTimeStep: current).ShouldBeNull();
        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current + 1), Now, lastUsedTimeStep: current).ShouldBe(current + 1);

        Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, current), Now, lastUsedTimeStep: current - 1).ShouldBe(current);
    }

    [Fact]
    public void FindMatchingStep_refuses_a_code_of_another_secret()
    {
        var otherSecret = Encoding.ASCII.GetBytes("09876543210987654321");
        var current = Totp.TimeStepAt(Now);
        var otherCode = Totp.ComputeCode(otherSecret, current);

        // Guard: the two secrets happen to give different codes at the steps the verifier tries.
        Totp.ComputeCode(RfcSecret, current - 1).ShouldNotBe(otherCode);
        Totp.ComputeCode(RfcSecret, current).ShouldNotBe(otherCode);
        Totp.ComputeCode(RfcSecret, current + 1).ShouldNotBe(otherCode);

        Totp.FindMatchingStep(RfcSecret, otherCode, Now, lastUsedTimeStep: null).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("１２３４５６")]
    [InlineData(" 123456")]
    [InlineData("123456 ")]
    [InlineData("12 456")]
    public void FindMatchingStep_refuses_malformed_codes(string code)
    {
        Totp.FindMatchingStep(RfcSecret, code, Now, lastUsedTimeStep: null).ShouldBeNull();
    }

    [Fact]
    public void FindMatchingStep_does_not_trim_or_pad_the_code()
    {
        var code = Totp.ComputeCode(RfcSecret, Totp.TimeStepAt(Now));

        Totp.FindMatchingStep(RfcSecret, code, Now, lastUsedTimeStep: null).ShouldNotBeNull();
        Totp.FindMatchingStep(RfcSecret, " " + code, Now, lastUsedTimeStep: null).ShouldBeNull();
        Totp.FindMatchingStep(RfcSecret, code[..5], Now, lastUsedTimeStep: null).ShouldBeNull();
    }

    [Fact]
    public void NewSecret_is_twenty_random_bytes()
    {
        var first = Totp.NewSecret();
        var second = Totp.NewSecret();

        first.Length.ShouldBe(20);
        second.Length.ShouldBe(20);
        first.SequenceEqual(second).ShouldBeFalse();
    }

    [Fact]
    public void ComputeCode_refuses_an_empty_secret_and_an_unsupported_length()
    {
        Should.Throw<ArgumentException>(() => Totp.ComputeCode(ReadOnlySpan<byte>.Empty, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => Totp.ComputeCode(RfcSecret, 1, digits: 5));
        Should.Throw<ArgumentOutOfRangeException>(() => Totp.ComputeCode(RfcSecret, 1, digits: 9));
    }
}
