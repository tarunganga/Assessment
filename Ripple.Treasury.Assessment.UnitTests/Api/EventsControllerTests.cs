using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Ripple.Treasury.Assessment.Api.Controllers;
using Ripple.Treasury.Assessment.Api.Models.Requests;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Inputs;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.UnitTests.Api;

public class EventsControllerTests
{
    private readonly IEventService _events = Substitute.For<IEventService>();
    private readonly ITicketPurchaseService _purchases = Substitute.For<ITicketPurchaseService>();
    private readonly EventsController _controller;

    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid TierId = Guid.Parse("01900000-0000-7000-8000-0000000000a1");
    private static readonly Guid PurchaseId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");

    // xUnit builds the class once per test, so the controller and its substitutes
    // start clean for every one of them.
    public EventsControllerTests()
    {
        ProblemDetailsFactory factory = Substitute.For<ProblemDetailsFactory>();

        factory.CreateValidationProblemDetails(
                Arg.Any<HttpContext>(), Arg.Any<ModelStateDictionary>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(new ValidationProblemDetails { Status = StatusCodes.Status400BadRequest });

        _controller = new EventsController(_events, _purchases)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = factory
        };
    }

    private static SaveEventRequest NewRequest()
    {
        return new SaveEventRequest
        {
            Name = "Opening Night",
            Description = "A show",
            Venue = "Royal Arena",
            StartsAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = 100,
            PricingTiers =
            [
                new SavePricingTierRequest { Name = "GA", PriceAmount = 50m, PriceCurrency = "USD", Allocation = 60 },
                new SavePricingTierRequest { Name = "VIP", PriceAmount = 150m, PriceCurrency = "USD", Allocation = 40 }
            ]
        };
    }

    private static PurchaseTicketsRequest NewPurchase(int quantity = 2)
    {
        return new PurchaseTicketsRequest
        {
            PurchaserEmail = "buyer@example.com",
            PurchaseItems = [new PurchaseItemRequest { PricingTierId = TierId, Quantity = quantity }]
        };
    }

    [Fact]
    public async Task Create_returns_201_pointing_at_the_new_event()
    {
        _events.CreateAsync(Arg.Any<CreateEventInput>(), Arg.Any<CancellationToken>()).Returns(EventId);
        _events.GetAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new EventDetail { Id = EventId, Name = "Opening Night" });

        IActionResult result = await _controller.CreateEventAsync(NewRequest(), default);

        CreatedAtActionResult created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(EventsController.GetEventByIdAsync), created.ActionName);
        Assert.Equal(EventId, created.RouteValues!["eventId"]);
        Assert.Equal(EventId, Assert.IsType<EventDetail>(created.Value).Id);
    }

    [Fact]
    public async Task Create_hands_the_service_the_mapped_request()
    {
        _events.CreateAsync(Arg.Any<CreateEventInput>(), Arg.Any<CancellationToken>()).Returns(EventId);

        await _controller.CreateEventAsync(NewRequest(), default);

        await _events.Received(1).CreateAsync(
            Arg.Is<CreateEventInput>(i =>
                i.Name == "Opening Night"
                && i.Venue == "Royal Arena"
                && i.TotalCapacity == 100
                && i.PricingTiers.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_reads_the_event_back_rather_than_echoing_the_request()
    {
        _events.CreateAsync(Arg.Any<CreateEventInput>(), Arg.Any<CancellationToken>()).Returns(EventId);

        await _controller.CreateEventAsync(NewRequest(), default);

        // The body must show what was stored, including server-set fields.
        await _events.Received(1).GetAsync(EventId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_returns_the_event_as_it_now_stands()
    {
        _events.GetAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new EventDetail { Id = EventId, Name = "Renamed" });

        IActionResult result = await _controller.UpdateEventAsync(EventId, NewRequest(), default);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Renamed", Assert.IsType<EventDetail>(ok.Value).Name);

        await _events.Received(1).UpdateAsync(
            EventId, Arg.Any<UpdateEventInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_returns_204_with_no_body()
    {
        IActionResult result = await _controller.DeleteEventAsync(EventId, default);

        Assert.IsType<NoContentResult>(result);
        await _events.Received(1).DeleteAsync(EventId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publish_returns_the_published_event()
    {
        _events.GetAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new EventDetail { Id = EventId, Status = "Published" });

        IActionResult result = await _controller.PublishEventAsync(EventId, default);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Published", Assert.IsType<EventDetail>(ok.Value).Status);
        await _events.Received(1).PublishAsync(EventId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public async Task A_page_below_one_is_pulled_up_to_the_first_page(int requested, int expected)
    {
        await _controller.GetAllEventsAsync(null, null, requested, 20, default);

        await _events.Received(1).ListAsync(
            null, null, expected, 20, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(101, 20)]
    [InlineData(100, 100)]
    [InlineData(50, 50)]
    public async Task A_page_size_outside_one_to_a_hundred_falls_back_to_twenty(int requested, int expected)
    {
        await _controller.GetAllEventsAsync(null, null, 1, requested, default);

        await _events.Received(1).ListAsync(
            null, null, 1, expected, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_passes_both_filters_through_untouched()
    {
        DateTimeOffset from = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await _controller.GetAllEventsAsync(from, "Royal Arena", 2, 10, default);

        await _events.Received(1).ListAsync(
            from, "Royal Arena", 2, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Availability_and_the_sales_report_return_what_the_service_gives()
    {
        _events.GetAvailabilityAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new EventAvailability { EventId = EventId, TotalAvailable = 42 });
        _events.GetSalesReportAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new SalesReport { EventId = EventId, TotalRevenue = 250m });

        OkObjectResult availability =
            Assert.IsType<OkObjectResult>(await _controller.GetEventAvailabilityAsync(EventId, default));
        OkObjectResult report =
            Assert.IsType<OkObjectResult>(await _controller.GetEventSalesReportAsync(EventId, default));

        Assert.Equal(42, Assert.IsType<EventAvailability>(availability.Value).TotalAvailable);
        Assert.Equal(250m, Assert.IsType<SalesReport>(report.Value).TotalRevenue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_purchase_without_an_idempotency_key_is_rejected_before_the_service_is_called(string? key)
    {
        IActionResult result = await _controller
            .PurchaseTicketsAsync(EventId, NewPurchase(), key, default);

        ObjectResult problem = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);

        await _purchases.DidNotReceive().PurchaseAsync(
            Arg.Any<PurchaseTicketsInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_first_purchase_returns_201_pointing_at_the_purchases_route()
    {
        _purchases.PurchaseAsync(Arg.Any<PurchaseTicketsInput>(), Arg.Any<CancellationToken>())
            .Returns(new PurchaseResult { PurchaseId = PurchaseId, IsReplay = false });
        _purchases.GetAsync(PurchaseId, Arg.Any<CancellationToken>())
            .Returns(new PurchaseDetail { Id = PurchaseId, TotalAmount = 100m });

        IActionResult result = await _controller.PurchaseTicketsAsync(
            EventId, NewPurchase(), "order-100", default);

        CreatedAtActionResult created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal("Purchases", created.ControllerName);
        Assert.Equal(PurchaseId, created.RouteValues!["purchaseId"]);
        Assert.False(_controller.Response.Headers.ContainsKey("Idempotent-Replay"));
    }

    [Fact]
    public async Task A_replayed_purchase_returns_200_and_flags_itself_in_a_header()
    {
        _purchases.PurchaseAsync(Arg.Any<PurchaseTicketsInput>(), Arg.Any<CancellationToken>())
            .Returns(new PurchaseResult { PurchaseId = PurchaseId, IsReplay = true });
        _purchases.GetAsync(PurchaseId, Arg.Any<CancellationToken>())
            .Returns(new PurchaseDetail { Id = PurchaseId, TotalAmount = 100m });

        IActionResult result = await _controller.PurchaseTicketsAsync(
            EventId, NewPurchase(), "order-100", default);

        // A replay is not a new resource, so it must not answer 201.
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(PurchaseId, Assert.IsType<PurchaseDetail>(ok.Value).Id);
        Assert.Equal("true", _controller.Response.Headers["Idempotent-Replay"]);
    }

    [Fact]
    public async Task The_purchase_carries_the_route_event_id_and_the_header_key()
    {
        _purchases.PurchaseAsync(Arg.Any<PurchaseTicketsInput>(), Arg.Any<CancellationToken>())
            .Returns(new PurchaseResult { PurchaseId = PurchaseId, IsReplay = false });
        _purchases.GetAsync(PurchaseId, Arg.Any<CancellationToken>())
            .Returns(new PurchaseDetail { Id = PurchaseId });

        await _controller.PurchaseTicketsAsync(EventId, NewPurchase(3), "order-100", default);

        // The event comes from the route, never from the body.
        await _purchases.Received(1).PurchaseAsync(
            Arg.Is<PurchaseTicketsInput>(i =>
                i.EventId == EventId
                && i.IdempotencyKey == "order-100"
                && i.PurchaserEmail == "buyer@example.com"
                && i.PurchaseItems.Count == 1
                && i.PurchaseItems[0].PricingTierId == TierId
                && i.PurchaseItems[0].Quantity == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_service()
    {
        using CancellationTokenSource source = new();

        await _controller.GetEventByIdAsync(EventId, source.Token);

        await _events.Received(1).GetAsync(EventId, source.Token);
    }
}
