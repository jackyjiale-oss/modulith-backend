using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Concurrency;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Concurrency;
using TemplateName.Web.Common.Results;

namespace TemplateName.UnitTests.Web;

public sealed class EntityTagResultTests
{
    private static readonly EntityTag Tag = new("q3Y0b7lX2m9k1Rr0cVn8Aw");

    [Fact]
    public async Task No_content_with_an_entity_tag_is_204_with_the_etag()
    {
        var context = NewContext();

        await TypedResults.NoContent().WithEntityTag(Tag).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
        context.Response.Headers.ETag.ToString().ShouldBe("\"q3Y0b7lX2m9k1Rr0cVn8Aw\"");
    }

    [Fact]
    public async Task Ok_with_an_entity_tag_keeps_its_body()
    {
        var context = NewContext();

        await TypedResults.Ok(new { name = "x" }).WithEntityTag(Tag).ExecuteAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.ETag.ToString().ShouldBe("\"q3Y0b7lX2m9k1Rr0cVn8Aw\"");
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync(TestContext.Current.CancellationToken))
            .ShouldBe("{\"name\":\"x\"}");
    }

    [Fact]
    public void A_failed_precondition_is_a_412_problem()
    {
        var problem = Result.Failure(ConcurrencyErrors.PreconditionFailed).ToProblem().ShouldBeOfType<ProblemHttpResult>();

        problem.StatusCode.ShouldBe(StatusCodes.Status412PreconditionFailed);
        problem.ProblemDetails.Extensions["code"].ShouldBe("concurrency.precondition_failed");
    }

    private static DefaultHttpContext NewContext()
        => new()
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
}
