using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class PhoneVerificationCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    [Fact]
    public void NewCode_is_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            PhoneVerificationCode.NewCode().ShouldMatch(@"^[0-9]{6}\z");
        }
    }

    [Fact]
    public void Matches_only_the_issued_code()
    {
        var code = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        code.Matches("042137").ShouldBeTrue();
        code.Matches("042138").ShouldBeFalse();
        code.Matches("42137").ShouldBeFalse();
        code.Matches(string.Empty).ShouldBeFalse();
        code.Salt.Length.ShouldBe(16);
        code.CodeHash.Length.ShouldBe(32);
    }

    [Fact]
    public void The_same_code_issued_twice_has_different_hashes()
    {
        var first = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);
        var second = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        first.CodeHash.ShouldNotBe(second.CodeHash);
    }

    [Fact]
    public void ToString_never_contains_the_code_or_number()
    {
        var code = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        var text = code.ToString();

        text.ShouldContain(code.Id.ToString());
        text.ShouldContain(UserId.ToString());
        text.ShouldNotContain("042137");
        text.ShouldNotContain("60123456789");
    }

    [Fact]
    public void IsPending_until_expiry_exclusive()
    {
        var code = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        code.ExpiresAt.ShouldBe(Now.AddMinutes(10));
        code.IsPending(Now.AddMinutes(10).AddTicks(-1)).ShouldBeTrue();
        code.IsPending(Now.AddMinutes(10)).ShouldBeFalse();
    }

    [Fact]
    public void Invalidate_keeps_the_first_time()
    {
        var code = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        code.Invalidate(Now.AddMinutes(1));
        code.Invalidate(Now.AddMinutes(2));

        code.InvalidatedAt.ShouldBe(Now.AddMinutes(1));
        code.IsPending(Now.AddMinutes(1)).ShouldBeFalse();
    }

    [Fact]
    public void Consume_ends_it()
    {
        var code = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        code.Consume(Now.AddMinutes(1));

        code.ConsumedAt.ShouldBe(Now.AddMinutes(1));
        code.IsPending(Now.AddMinutes(1)).ShouldBeFalse();
        code.Invalidate(Now.AddMinutes(2));
        code.InvalidatedAt.ShouldBeNull();
    }

    [Fact]
    public void Issue_keeps_the_number_and_owner()
    {
        var code = PhoneVerificationCode.Issue(UserId, "+60123456789", "042137", "protected", TimeSpan.FromMinutes(10), Now);

        code.UserId.ShouldBe(UserId);
        code.PhoneNumber.ShouldBe("+60123456789");
        code.CreatedAt.ShouldBe(Now);
        code.AttemptCount.ShouldBe(0);
    }
}
