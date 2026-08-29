using Microsoft.AspNetCore.Mvc;

namespace Ripple.Treasury.Assesment.Controllers;

[ApiController]
[Route("events")]
public class EventsController : ControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> GetAllEventsAsync()
    {
        return Ok();
    }
}