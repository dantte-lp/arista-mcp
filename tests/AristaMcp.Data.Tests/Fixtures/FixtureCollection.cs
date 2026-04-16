using Xunit;

namespace AristaMcp.Data.Tests.Fixtures;

[CollectionDefinition("Pgvector")]
public class FixtureCollection : ICollectionFixture<PgvectorFixture>
{
}
