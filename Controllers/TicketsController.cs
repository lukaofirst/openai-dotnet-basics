using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OpenAiBasicsApi.Controllers;

[ApiController]
[Route("api")]
[ServiceFilter(typeof(RequestGateFilter))]
public sealed class TicketsController(
    TicketClassifier classifier, IWebHostEnvironment environment) : ControllerBase
{
    [HttpPost("tickets/classify")]
    public async Task<IActionResult> Classify([FromBody] TicketRequest request, CancellationToken ct)
    {
        decimal before = classifier.EstimatedUsd;
        string label = await classifier.ClassifyAsync(request.Text ?? "", ct);
        return Ok(new { label, estimatedUsd = classifier.EstimatedUsd - before });
    }

    [HttpPost("evals")]
    public async Task<IActionResult> Evaluate(CancellationToken ct)
    {
        // Development only: evals can incur charges and consume the shared budget.
        if (!environment.IsDevelopment()) return NotFound();
        return Ok(await Evals.RunAsync(classifier, ct));
    }
}

public record TicketRequest(string? Text);

// Registered as a singleton, so every controller instance shares the same gate.
// Hold it for an entire eval run to keep per-case cost differences accurate.
public sealed class RequestGateFilter : IAsyncActionFilter, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        await gate.WaitAsync(context.HttpContext.RequestAborted);
        try { await next(); }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
