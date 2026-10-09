using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using TemplateName.Web.Common.Identity;

namespace TemplateName.UnitTests.Web;

public sealed class HttpContextCurrentUserTests
{
    private static readonly Guid UserId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void Reads_user_id_from_sub_claim()
    {
        var sut = CreateSut(new Claim("sub", UserId.ToString()));

        sut.UserId.ShouldBe(UserId);
        sut.IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public void Falls_back_to_name_identifier_claim()
    {
        var sut = CreateSut(new Claim(ClaimTypes.NameIdentifier, UserId.ToString()));

        sut.UserId.ShouldBe(UserId);
    }

    [Fact]
    public void Anonymous_request_has_no_user()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext());
        var sut = new HttpContextCurrentUser(accessor);

        sut.UserId.ShouldBeNull();
        sut.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public void Non_guid_subject_yields_no_user_id()
    {
        var sut = CreateSut(new Claim("sub", "not-a-guid"));

        sut.UserId.ShouldBeNull();
    }

    [Fact]
    public void SessionId_is_read_from_the_sid_claim_and_null_when_malformed()
    {
        var sessionId = Guid.Parse("0199b0a3-6000-7d4f-8b62-3b9f5d7c8e21");

        CreateSut(new Claim("sub", UserId.ToString()), new Claim("sid", sessionId.ToString())).SessionId.ShouldBe(sessionId);
        CreateSut(new Claim("sub", UserId.ToString()), new Claim("sid", "not-a-guid")).SessionId.ShouldBeNull();
        CreateSut(new Claim("sub", UserId.ToString())).SessionId.ShouldBeNull();
    }

    [Fact]
    public void No_request_means_no_session()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        new HttpContextCurrentUser(accessor).SessionId.ShouldBeNull();
    }

    private static HttpContextCurrentUser CreateSut(params Claim[] claims)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        });
        return new HttpContextCurrentUser(accessor);
    }
}
