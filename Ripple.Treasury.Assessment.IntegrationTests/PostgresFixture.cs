using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Ripple.Treasury.Assessment.Infrastructure;
using Testcontainers.PostgreSql;

namespace Ripple.Treasury.Assessment.IntegrationTests;

public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("docker.io/library/postgres:18-alpine")
        .WithDatabase("ticketing_test")
        .WithUsername("ripple")
        .WithPassword("ripple_test")
        .WithCommand("-c", "max_connections=300")
        .Build();

    private Respawner _respawner = null!;
    private NpgsqlConnection _respawnConnection = null!;

    private string ConnectionString { get; set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString() + ";Maximum Pool Size=250";

        await using (TicketingDbContext db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        _respawnConnection = new NpgsqlConnection(ConnectionString);
        await _respawnConnection.OpenAsync();

        _respawner = await Respawner.CreateAsync(_respawnConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")]
        });
    }

    public async Task DisposeAsync()
    {
        await _respawnConnection.DisposeAsync();
        await _container.DisposeAsync();
    }

    // Empty schema per test
    public async Task ResetAsync()
    {
        await _respawner.ResetAsync(_respawnConnection);
    }

    public TicketingDbContext CreateDbContext()
    {
        DbContextOptions<TicketingDbContext> options =
            new DbContextOptionsBuilder<TicketingDbContext>()
                .UseNpgsql(ConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;

        return new TicketingDbContext(options);
    }
}
