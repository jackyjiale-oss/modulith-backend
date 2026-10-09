using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using TemplateName.Infrastructure.Common.Resources;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Sample.Endpoints;
using TemplateName.Modules.Sample.Resources;
using TemplateName.Web.Common.Resources;

namespace TemplateName.IntegrationTests.Localization;

public sealed class LocalizationTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string BaseRoute = "/api/v1/sample/leave-requests";

    // A saved locale the API does not support. The saved locale wins over Accept-Language (decision D7); this one leaves the choice to
    // the header, which is what these tests are about. Saved_locale_claim_wins_over_accept_language (PipelineTests) covers the claim.
    private const string UnsupportedSavedLocale = "fr";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync(["sample.leave_request.view", "sample.leave_request.create"], UnsupportedSavedLocale);
    }

    [Fact]
    public async Task Malay_request_gets_malay_detail_and_unchanged_code()
    {
        var id = Guid.NewGuid();

        using var response = await GetAsync($"{BaseRoute}/{id}", "ms");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("leave.not_found");
        body.GetProperty("params").GetProperty("id").GetGuid().ShouldBe(id);
        var detail = body.GetProperty("detail").GetString()!;
        detail.ShouldNotBe($"Leave request '{id}' was not found.");
        detail.ShouldContain(id.ToString());
        response.Content.Headers.ContentLanguage.ShouldBe(["ms"]);
    }

    [Fact]
    public async Task Chinese_region_gets_simplified_chinese()
    {
        var id = Guid.NewGuid();

        using var response = await GetAsync($"{BaseRoute}/{id}", "zh-CN,zh;q=0.9");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentLanguage.ShouldBe(["zh-Hans"]);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("detail").GetString().ShouldBe(Resource(typeof(SampleErrorMessages), "leave.not_found", "zh-Hans").Replace("{id}", id.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsupported_language_falls_back_to_english()
    {
        var id = Guid.NewGuid();

        using var response = await GetAsync($"{BaseRoute}/{id}", "fr-FR");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentLanguage.ShouldBe(["en"]);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("detail").GetString().ShouldBe($"Leave request '{id}' was not found.");
    }

    [Fact]
    public async Task Malformed_accept_language_falls_back_to_english()
    {
        var id = Guid.NewGuid();

        using var response = await GetAsync($"{BaseRoute}/{id}", "!!!;q=abc");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("detail").GetString().ShouldBe($"Leave request '{id}' was not found.");
    }

    [Fact]
    public async Task Exception_handler_responses_are_localized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseRoute)
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Accept-Language", "ms");

        using var response = await Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("request.malformed");
        body.GetProperty("detail").GetString().ShouldBe(Resource(typeof(CommonErrorMessages), "request.malformed", "ms"));
    }

    [Fact]
    public async Task Validation_messages_are_localized()
    {
        var english = await SubmitInvalidAsync("en");
        var malay = await SubmitInvalidAsync("ms");

        malay.GetProperty("code").GetString().ShouldBe("validation.failed");
        malay.GetProperty("detail").GetString().ShouldBe(Resource(typeof(CommonErrorMessages), "validation.failed", "ms"));
        var malayReason = malay.GetProperty("errors").GetProperty("reason")[0].GetString();
        malayReason.ShouldNotBeNullOrEmpty();
        malayReason.ShouldNotBe(english.GetProperty("errors").GetProperty("reason")[0].GetString());
    }

    [Fact]
    public async Task Enum_values_are_not_localized()
    {
        using var submitted = await Client.PostAsJsonAsync(BaseRoute, Valid(), Ct);
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        using var response = await GetAsync($"{BaseRoute}/{id}", "ms");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task Rate_limit_responses_are_localized()
    {
        await using var limited = Factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:GlobalPermitLimit", "1"));
        using var client = limited.CreateClient();
        // Signed in: for an anonymous caller the fallback policy answers 401 before the limiter counts the request. The token's saved
        // locale wins over Accept-Language (decision D7), so it carries the same language.
        var token = TestAccessTokens.Issue(limited.Services, locale: "zh-Hans").Value;

        using var allowed = await GetAsync(client, "/api/v1/does-not-exist", "zh-Hans", token);
        using var response = await GetAsync(client, "/api/v1/does-not-exist", "zh-Hans", token);

        allowed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await allowed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString()
            .ShouldBe(Resource(typeof(CommonErrorMessages), "http.404", "zh-Hans"));
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("detail").GetString()
            .ShouldBe(Resource(typeof(CommonErrorMessages), "rate_limit.exceeded", "zh-Hans"));
    }

    [Fact]
    public async Task Idempotency_responses_are_localized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseRoute) { Content = JsonContent.Create(Valid()) };
        request.Headers.Add("Idempotency-Key", new string('k', 101));
        request.Headers.TryAddWithoutValidation("Accept-Language", "ms");

        using var response = await Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("idempotency.invalid_key");
        body.GetProperty("detail").GetString().ShouldBe(Resource(typeof(InfrastructureErrorMessages), "idempotency.invalid_key", "ms"));
    }

    [Fact]
    public void Background_default_culture_is_english()
    {
        CultureInfo.DefaultThreadCurrentUICulture!.Name.ShouldBe("en");
        CultureInfo.DefaultThreadCurrentCulture!.Name.ShouldBe("en");
    }

    private static SubmitLeaveRequestRequest Valid()
        => new(Guid.NewGuid(), new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), "Family trip");

    /// <summary>The translation itself (no parent fallback), so a missing entry fails instead of comparing English with English.</summary>
    private static string Resource(Type resource, string key, string culture)
        => new ResourceManager(resource.FullName!, resource.Assembly)
            .GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false)!
            .GetString(key)!;

    private Task<HttpResponseMessage> GetAsync(string url, string language) => GetAsync(Client, url, language);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string language, string? accessToken = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Language", language);
        if (accessToken is not null)
        {
            request.WithBearer(accessToken);
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task<JsonElement> SubmitInvalidAsync(string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseRoute) { Content = JsonContent.Create(Valid() with { Reason = "" }) };
        request.Headers.TryAddWithoutValidation("Accept-Language", language);

        using var response = await Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
