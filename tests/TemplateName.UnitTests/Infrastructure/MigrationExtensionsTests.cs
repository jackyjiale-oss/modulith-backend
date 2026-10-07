using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Persistence;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class MigrationExtensionsTests
{
    [Fact]
    public async Task Missing_connection_string_fails_with_options_validation_error()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = "" })
            .Build();
        await using var services = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();

        var exception = await Should.ThrowAsync<OptionsValidationException>(
            () => services.MigrateModuleDatabasesAsync(TestContext.Current.CancellationToken));

        exception.OptionsType.Name.ShouldBe("ConnectionStringsOptions");
    }
}
