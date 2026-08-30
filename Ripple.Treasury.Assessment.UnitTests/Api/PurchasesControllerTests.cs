using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Ripple.Treasury.Assessment.Api.Controllers;
using Ripple.Treasury.Assessment.Services;
using Ripple.Treasury.Assessment.Services.Exceptions;
using Ripple.Treasury.Assessment.Services.Projections;

namespace Ripple.Treasury.Assessment.UnitTests.Api;

public class PurchasesControllerTests
{
    private readonly ITicketPurchaseService _purchases = Substitute.For<ITicketPurchaseService>();
    private readonly PurchasesController _controller;

    private static readonly Guid PurchaseId = Guid.Parse("01900000-0000-7000-8000-0000000000b1");
    private static readonly Guid EventId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");

    public PurchasesControllerTests()
    {
        _controller = new PurchasesController(_purchases)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Get_returns_the_purchase_the_service_found()
    {
        _purchases.GetAsync(PurchaseId, Arg.Any<CancellationToken>())
            .Returns(new PurchaseDetail
            {
                Id = PurchaseId,
                EventId = EventId,
                TotalAmount = 250m,
                Currency = "USD",
                Status = "Completed"
            });

        IActionResult result = await _controller.GetPurchaseByIdAsync(PurchaseId, default);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PurchaseDetail detail = Assert.IsType<PurchaseDetail>(ok.Value);

        Assert.Equal(PurchaseId, detail.Id);
        Assert.Equal(250m, detail.TotalAmount);
        Assert.Equal("USD", detail.Currency);
    }

    [Fact]
    public async Task Get_asks_the_service_for_the_id_in_the_route()
    {
        await _controller.GetPurchaseByIdAsync(PurchaseId, default);

        await _purchases.Received(1).GetAsync(PurchaseId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_cancellation_token_reaches_the_service()
    {
        using CancellationTokenSource source = new();

        await _controller.GetPurchaseByIdAsync(PurchaseId, source.Token);

        await _purchases.Received(1).GetAsync(PurchaseId, source.Token);
    }

    [Fact]
    public async Task A_missing_purchase_is_left_for_the_exception_handler()
    {
        _purchases.GetAsync(PurchaseId, Arg.Any<CancellationToken>())
            .Returns<PurchaseDetail>(_ => throw new PurchaseNotFoundException(PurchaseId));

        // The controller does not catch - ProblemDetailsExceptionHandler turns
        // this into the 404, and ErrorMappingTests covers that half.
        await Assert.ThrowsAsync<PurchaseNotFoundException>(
            () => _controller.GetPurchaseByIdAsync(PurchaseId, default));
    }
}
