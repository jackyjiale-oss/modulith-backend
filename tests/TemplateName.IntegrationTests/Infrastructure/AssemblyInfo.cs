using TemplateName.IntegrationTests.Infrastructure;
using Xunit.Sdk;
using Xunit.v3;

// One host for the whole assembly; tests share it and therefore run one at a time.
// (xUnit v3 obsoletes CollectionBehavior.DisableTestParallelization in favor of the Parallelization attribute.)
[assembly: AssemblyFixture(typeof(IntegrationTestWebAppFactory))]
[assembly: Parallelization(Mode = ParallelMode.None)]
