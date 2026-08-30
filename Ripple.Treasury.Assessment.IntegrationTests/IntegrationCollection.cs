namespace Ripple.Treasury.Assessment.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class IntegrationCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Integration";
}
