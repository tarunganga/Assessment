using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ripple.Treasury.Assessment.Infrastructure.Extensions;

public static class DiExtensions
{
    public static void AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TicketingDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseSnakeCaseNamingConvention();
        });

        services.AddHealthChecks()
            .AddDbContextCheck<TicketingDbContext>("database", tags: ["ready"]);
    }
}