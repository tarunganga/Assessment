namespace Ripple.Treasury.Assessment.IntegrationTests.Fixtures;

[CollectionDefinition(Name, DisableParallelization = true)]
public class IntegrationCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Integration";
}
