using Microsoft.AspNetCore.Mvc;
using OpenAI.Responses;

namespace OpenAiBasicsApi.Controllers;

[ApiController]
[Route("api/hello")]
public sealed class HelloController(
    ResponsesClient responses, IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Hello(CancellationToken ct)
    {
        var model = configuration["OPENAI_MODEL"] ?? "gpt-6-luna";
        ResponseResult response = await responses.CreateResponseAsync(
            model, "Explain async/await in one sentence.", cancellationToken: ct);
        return Ok(new { text = response.GetOutputText() });
    }
}
