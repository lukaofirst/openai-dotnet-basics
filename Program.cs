using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using OpenAI;
using OpenAI.Moderations;
using OpenAI.Responses;
using OpenAiBasicsApi;
using OpenAiBasicsApi.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var key = builder.Configuration["OPENAI_API_KEY"]
    ?? throw new InvalidOperationException("Set OPENAI_API_KEY.");
var model = builder.Configuration["OPENAI_MODEL"] ?? "gpt-6.1-sol";
var chatModel = builder.Configuration["OPENAI_CHAT_MODEL"] ?? model;

// Fail at startup if cost configuration is missing or invalid.
var rates = new TokenRates(
    ReadRate(builder.Configuration, "OPENAI_INPUT_USD_PER_MILLION"),
    ReadRate(builder.Configuration, "OPENAI_CACHED_INPUT_USD_PER_MILLION"),
    ReadRate(builder.Configuration, "OPENAI_OUTPUT_USD_PER_MILLION"));

builder.Services.AddControllers();
builder.Services.AddSingleton<RequestGateFilter>();
// One attempt per SDK call makes this teaching example easier to account for.
builder.Services.AddSingleton(new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
{
    RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
}));
builder.Services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetResponsesClient());
builder.Services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetChatClient(chatModel));
builder.Services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetEmbeddingClient("text-embedding-3-small"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetModerationClient("omni-moderation-latest"));
builder.Services.AddSingleton(sp => new TicketClassifier(
    sp.GetRequiredService<ResponsesClient>(), sp.GetRequiredService<ModerationClient>(),
    model, rates, sp.GetRequiredService<ILogger<TicketClassifier>>()));


// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Translate failures to HTTP responses without exposing raw SDK exception bodies.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The caller disconnected; there is no response to write.
    }
    catch (Exception error)
    {
        (int Status, string Title) failure = error switch
        {
            GuardrailException g when g.Code is "empty_input" or "input_too_long"
                => (400, g.Code),
            GuardrailException g when g.Code == "input_moderation" => (422, g.Code),
            GuardrailException g => (502, g.Code),
            ClientResultException e when e.Status == 429
                => (503, "Upstream rate, credit, quota, or spending limit reached."),
            ClientResultException => (502, "OpenAI request failed. Check server configuration."),
            OperationCanceledException => (504, "Classification timed out."),
            HttpRequestException => (502, "Could not reach OpenAI."),
            InvalidOperationException => (503, "Service unavailable. Check limits and accounting."),
            _ => (500, "Unexpected server error.")
        };
        app.Logger.LogWarning("Request failed: {ErrorType}; HTTP {Status}",
            error.GetType().Name, failure.Status);
        await Results.Problem(title: failure.Title, statusCode: failure.Status).ExecuteAsync(context);
    }
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

static decimal ReadRate(IConfiguration configuration, string name)
{
    if (!decimal.TryParse(configuration[name],
            NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate) || rate < 0)
        throw new InvalidOperationException($"Set {name} to a nonnegative USD rate.");
    return rate;
}
