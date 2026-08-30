using Microsoft.Extensions.DependencyInjection;
using Ripple.Treasury.Assessment.Services;

namespace Ripple.Treasury.Assessment.Services.Extensions;

public static class DiExtensions
{
    public static void RegisterServices(this IServiceCollection services)
    {
        services.AddScoped<IEventService, EventService>();
        services.AddScoped<ITicketPurchaseService, TicketPurchaseService>();
    }
}
