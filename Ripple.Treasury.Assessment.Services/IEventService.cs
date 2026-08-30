using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.Services;

public interface IEventService
{
    Task<Guid> CreateAsync(CreateEventInput input, CancellationToken cancellationToken);

    Task UpdateAsync(Guid eventId, UpdateEventInput input, CancellationToken cancellationToken);

    Task PublishAsync(Guid eventId, CancellationToken cancellationToken);

    Task DeleteAsync(Guid eventId, CancellationToken cancellationToken);

    Task<EventDetail> GetAsync(Guid eventId, CancellationToken cancellationToken);

    Task<PagedResult<EventSummary>> ListAsync(
        DateTimeOffset? from, string? venue, int page, int pageSize, CancellationToken cancellationToken);

    Task<EventAvailability> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken);

    Task<SalesReport> GetSalesReportAsync(Guid eventId, CancellationToken cancellationToken);
}
