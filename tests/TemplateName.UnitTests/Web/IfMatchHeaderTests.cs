using Microsoft.AspNetCore.Http.HttpResults;
using TemplateName.Application.Common.Concurrency;
using TemplateName.Web.Common.Concurrency;

namespace TemplateName.UnitTests.Web;

public sealed class IfMatchHeaderTests
{
    [Theory]
    [InlineData("\"abc\"", "abc", true)]
    [InlineData(" \"abc\" , \"def\" ", "def", true)]
    [InlineData("\"abc\",\"def\"", "abc", true)]
    [InlineData("\"abc\"", "xyz", false)]
    [InlineData("W/\"abc\"", "abc", false)]
    [InlineData("W/\"abc\", \"def\"", "abc", false)]
    [InlineData("W/\"abc\", \"def\"", "def", true)]
    [InlineData("\"\"", "", true)]
    public void Parses_tags(string header, string current, bool satisfied)
    {
        IfMatchHeader.TryParse(header, out var ifMatch).ShouldBeTrue();
        ifMatch.IsSatisfiedBy(new EntityTag(current)).ShouldBe(satisfied);
    }

    [Fact]
    public void A_header_of_weak_tags_only_is_a_precondition_no_version_meets()
    {
        IfMatchHeader.TryParse("W/\"abc\"", out var ifMatch).ShouldBeTrue();
        ifMatch.ShouldNotBeSameAs(IfMatch.None);
        ifMatch.IsSatisfiedBy(new EntityTag("abc")).ShouldBeFalse();
    }

    [Fact]
    public void Absent_is_none()
    {
        IfMatchHeader.TryParse(null, out var ifMatch).ShouldBeTrue();
        ifMatch.ShouldBeSameAs(IfMatch.None);
    }

    [Fact]
    public void Star_is_any()
    {
        IfMatchHeader.TryParse(" * ", out var ifMatch).ShouldBeTrue();
        ifMatch.ShouldBeSameAs(IfMatch.Any);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("\"abc")]
    [InlineData("abc\"")]
    [InlineData("*, \"abc\"")]
    [InlineData("\"abc\", *")]
    [InlineData("\"a\",,\"b\"")]
    [InlineData("\"a\",")]
    [InlineData("\"a b\"")]
    [InlineData("\"a\"b\"")]
    [InlineData("w/\"abc\"")]
    [InlineData("W/abc")]
    public void Malformed_is_refused(string header) => IfMatchHeader.TryParse(header, out _).ShouldBeFalse();

    [Fact]
    public void Malformed_answers_400_request_malformed()
    {
        var problem = IfMatchHeader.Malformed().ShouldBeOfType<ProblemHttpResult>();

        problem.StatusCode.ShouldBe(400);
        problem.ProblemDetails.Extensions["code"].ShouldBe("request.malformed");
    }
}
