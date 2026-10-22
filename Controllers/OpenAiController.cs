using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;
using OpenAI.Embeddings;
using OpenAI.Moderations;
using OpenAI.Responses;

namespace OpenAiBasicsApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OpenAiController(
    ResponsesClient responses, 
    ChatClient chat, 
    EmbeddingClient embeddings,
    ModerationClient moderation, 
    IConfiguration configuration
    ) : ControllerBase
{
    private readonly string model = configuration["OPENAI_MODEL"] ?? "gpt-6.1-sol";

    // 1. CreateResponseAsync: generate an explanation.
    // 2. GetOutputText: extract the answer. 3. Usage: report its token counts.
    [HttpPost("explanations")]
    public async Task<IActionResult> Explain([FromBody] TextRequest request, CancellationToken ct)
    {
        if (!ValidText(request.Text))
            return BadRequest(new { error = "Supply 1–2,000 characters of text." });

        var options = new CreateResponseOptions(model,
            [ResponseItem.CreateUserMessageItem(request.Text!)])
        {
            Instructions = "Explain the user's topic in two simple sentences.",
            MaxOutputTokenCount = 512,
            StoredOutputEnabled = false
        };
        ResponseResult response = await responses.CreateResponseAsync(options, ct);
        string text = response.GetOutputText();
        if (response.Status != ResponseStatus.Completed || string.IsNullOrWhiteSpace(text))
            return Problem("No complete explanation was returned.", statusCode: 502);

        var usage = response.Usage;
        return Ok(new
        {
            text,
            usage = usage is null ? null : new
            {
                inputTokens = usage.InputTokenCount,
                cachedInputTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0,
                outputTokens = usage.OutputTokenCount,
                reasoningTokens = usage.OutputTokenDetails?.ReasoningTokenCount ?? 0
            }
        });
    }

    // 4. CreateResponseStreamingAsync: forward text chunks as they arrive.
    [HttpPost("explanations/stream")]
    public async Task StreamExplanation([FromBody] TextRequest request, CancellationToken ct)
    {
        if (!ValidText(request.Text))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("Supply 1–2,000 characters of text.", ct);
            return;
        }

        var options = new CreateResponseOptions(model,
            [ResponseItem.CreateUserMessageItem(request.Text!)])
        {
            Instructions = "Explain the user's topic in two simple sentences.",
            MaxOutputTokenCount = 512,
            StoredOutputEnabled = false,
            StreamingEnabled = true
        };
        Response.ContentType = "text/plain; charset=utf-8";
        await foreach (StreamingResponseUpdate update in responses.CreateResponseStreamingAsync(options, ct))
        {
            if (update is StreamingResponseOutputTextDeltaUpdate delta)
            {
                await Response.WriteAsync(delta.Delta, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
    }

    // 5. CompleteChatAsync: answer a chat message.
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] TextRequest? request, CancellationToken ct)
    {
        if (request is null || !ValidText(request.Text))
            return BadRequest(new { error = "Supply 1–2,000 characters of text." });

        var result = await chat.CompleteChatAsync(
            [new SystemChatMessage("You are a concise support assistant."),
             new UserChatMessage(request.Text!)], cancellationToken: ct);
        ChatCompletion? reply = result?.Value;
        if (reply is null)
            return Problem("The chat service returned no completion.", statusCode: 502);

        string text = string.Concat(reply.Content.Select(part => part.Text));
        if (string.IsNullOrWhiteSpace(text))
            return Problem("The chat service returned no text.", statusCode: 502);

        return Ok(new { text });
    }

    // 6. GenerateEmbeddingAsync: obtain a vector for document similarity search.
    [HttpPost("embeddings")]
    public async Task<IActionResult> Embed([FromBody] TextRequest request, CancellationToken ct)
    {
        if (!ValidText(request.Text))
            return BadRequest(new { error = "Supply 1–2,000 characters of text." });

        OpenAIEmbedding embedding = await embeddings.GenerateEmbeddingAsync(request.Text!, cancellationToken: ct);
        return Ok(new { vector = embedding.ToFloats().ToArray() });
    }

    // 7. ClassifyTextAsync: obtain a safety signal before publishing a comment.
    [HttpPost("moderation")]
    public async Task<IActionResult> Moderate([FromBody] TextRequest request, CancellationToken ct)
    {
        if (!ValidText(request.Text))
            return BadRequest(new { error = "Supply 1–2,000 characters of text." });

        ModerationResult result = await moderation.ClassifyTextAsync(request.Text!, ct);
        return Ok(new { flagged = result.Flagged });
    }

    private static bool ValidText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 2_000;
}

public record TextRequest(string? Text);
