# OpenAI Basics API

An ASP.NET Core Web API demonstrating OpenAI integration in a .NET backend, from text generation and streaming to support-ticket classification with guardrails, usage accounting, and local behavioral evaluations.

Built with **.NET 10**, **ASP.NET Core controllers**, and **OpenAI .NET SDK 2.14.0**. The implementation focuses on application boundaries, validated model output, cancellation, and the operational consequences of calling an external AI service.

## Capabilities

| Endpoint | Behavior |
| --- | --- |
| `POST /api/hello` | Generates a short response from a fixed prompt. |
| `POST /api/openai/explanations` | Generates an explanation and returns reported token usage. |
| `POST /api/openai/explanations/stream` | Streams explanation text through incremental HTTP writes. |
| `POST /api/openai/chat` | Answers a message using system and user instructions. |
| `POST /api/openai/embeddings` | Converts text into a numeric vector. |
| `POST /api/openai/moderation` | Returns a content moderation flag. |
| `POST /api/tickets/classify` | Classifies a support ticket as `billing`, `technical`, or `other`. |
| `POST /api/evals` | Runs six local behavioral cases in Development. |

The feature endpoints accept `{ "text": "..." }`, except hello and evaluations, which require no request body. Text inputs must contain non-whitespace content and be at most 2,000 characters long.

## Architecture and engineering decisions

### Shared clients and explicit application boundaries

[Program.cs](Program.cs) registers a shared `OpenAIClient` and derived clients for Responses, Chat Completions, embeddings, and moderation. Controllers receive SDK clients through dependency injection; credentials, generation models, and token rates come from configuration. Request cancellation tokens propagate into asynchronous SDK calls.

[OpenAiController.cs](Controllers/OpenAiController.cs) contains the individual feature integrations. The explanation endpoint limits output to 512 tokens, disables stored output, and checks completion status and nonempty text before returning success. Its two-sentence instruction guides generation; C# does not enforce sentence count.

The feature endpoints demonstrate separate integration boundaries:

- **Chat:** creates a fresh message list per request, with no persisted history.
- **Embeddings:** returns vectors; storage and similarity search remain application work.
- **Moderation:** returns a safety signal; the ticket classifier shows how to enforce it.

### Streaming and the HTTP lifecycle

The streaming action consumes SDK updates with `await foreach`, forwards text deltas, and flushes the response body. Output is plain text rather than Server-Sent Events. After the response starts, an upstream failure may leave partial output and cannot reliably be converted into a new JSON problem response.

### Ticket classification with an enforced output contract

[TicketClassifier.cs](TicketClassifier.cs) separates classification rules and integration orchestration from the HTTP layer in [TicketsController.cs](Controllers/TicketsController.cs).

```text
Ticket → validate input → check local limits → count attempt
       → moderate input → generate → account for usage
       → validate completion and label → return domain result
```

| Label | Meaning | Example input |
| --- | --- | --- |
| `billing` | Payments, invoices, charges, or refunds | “I was charged twice.” |
| `technical` | Software errors, login failures, or broken features | “The login screen crashes every time.” |
| `other` | Other requests and messages | “Thanks for the help!” |

The prompt treats ticket text as untrusted data and instructs the model to ignore attempts to change classification rules. The application then validates the returned label against an allowlist. Prompt instructions guide behavior; the allowlist enforces the output contract. A permitted label alone does not establish classification accuracy.

### Usage accounting, concurrency, and failure handling

Classification uses a 45-second linked cancellation timeout, a maximum of 12 attempts, and a USD 0.05 soft budget. Attempts are counted before moderation, so failed upstream calls can still consume an attempt. SDK retries are disabled.

Estimated cost combines uncached input, cached input, and output token counts with configured per-million rates. Usage is recorded before incomplete or invalid model output is rejected. If generation fails with unknown usage, further classifications are blocked rather than treating the failed request as free.

A singleton `RequestGateFilter` uses `SemaphoreSlim` to serialize classifications and complete evaluation runs. Holding the gate for the entire suite keeps per-case cost differences isolated from concurrent requests.

Accounting has several limits:

- State lives in memory, resets on restart, and is not shared across replicas.
- Restarting does not reconcile unknown upstream usage.
- Only classification and its evaluations use this accounting.
- A request can exceed the soft budget; the estimate is not a guaranteed billing cap.

Middleware maps failures to HTTP problem responses without exposing raw SDK exception bodies. Invalid ticket input returns `400`, flagged input returns `422`, rejected output or upstream failures can return `502`, unavailable accounting or limits return `503`, and classification timeouts return `504`.

## OpenAI SDK methods, parameters, and setup

This reference covers the OpenAI package calls used in this repository, pinned to **2.14.0** in [OpenAiBasicsApi.csproj](OpenAiBasicsApi.csproj). Start with the client setup, then follow the method used by the endpoint you want to explore. The [official OpenAI SDK documentation](https://developers.openai.com/api/docs/libraries) provides installation guidance; the examples below match this checkout's SDK and code.

### Client setup and model selection

[Program.cs](Program.cs) constructs one root client and registers its feature clients as singletons:

```csharp
new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions
{
    RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
});
```

The setup fields control authentication and transport:

- **Credential:** `ApiKeyCredential(key)` supplies the key read through `IConfiguration`.
- **Options:** `OpenAIClientOptions.RetryPolicy` controls automatic retries. `maxRetries: 0` disables them, making attempts easier to account for.
- **Supporting types:** `ApiKeyCredential` and `ClientRetryPolicy` come from `System.ClientModel`.

Constructing clients does not generate output or preserve conversation history.

| Factory method | Parameters used here | Purpose and use case |
| --- | --- | --- |
| `GetResponsesClient()` | None. Each generation request supplies its model. | Obtain the client for hello, explanations, streaming, and ticket classification. |
| `GetChatClient(chatModel)` | `model`: `OPENAI_CHAT_MODEL`, falling back to the main model. | Obtain a Chat Completions client for role-based messages. The model is selected when the client is created. |
| `GetEmbeddingClient("text-embedding-3-small")` | `model`: the hardcoded embedding model name. | Obtain the client for turning text into vectors for similarity-based applications. |
| `GetModerationClient("omni-moderation-latest")` | `model`: the hardcoded moderation model name. | Obtain the client for content safety classification. |

See [Running locally](#running-locally) for configuration values and defaults:

- `OPENAI_*` keys configure this application; they are not SDK request properties.
- Embedding and moderation models are hardcoded and require code changes to override.
- Token rates support local classification estimates and are not sent to OpenAI.

### `ResponseItem.CreateUserMessageItem(text)` — build user input

This local factory wraps a string as a Responses API user message. It does not call the service. Explanations and classification pass the resulting item in the collection supplied to `new CreateResponseOptions(model, inputItems)`.

Use this to keep user content separate from application instructions. In classification, `text` is the untrusted ticket; the classification rules go into `Instructions`. That separation supports the prompt's intent, while the C# allowlist checks the returned label.

### `ResponsesClient.CreateResponseAsync(...)` — generate a complete result

Use a buffered response when the application needs the complete answer before returning HTTP success, such as a short explanation or a validated ticket label. The repository uses two overloads:

```csharp
// HelloController: model and a plain-text prompt.
ResponseResult response = await responses.CreateResponseAsync(
    model, "Explain async/await in one sentence.", cancellationToken: ct);

// OpenAiController and TicketClassifier: explicit request options.
var options = new CreateResponseOptions(model,
    [ResponseItem.CreateUserMessageItem(text)])
{
    Instructions = "Explain the user's topic in two simple sentences.",
    MaxOutputTokenCount = 512,
    StoredOutputEnabled = false
};
ResponseResult explanation = await responses.CreateResponseAsync(options, ct);
```

| Parameter or option | Value used here | Meaning |
| --- | --- | --- |
| `model` | Main configured model; see setup for fallbacks. | Selects the generation model for this request. |
| Plain-text prompt / input items | Fixed hello prompt, user topic, or ticket text. | Supplies the material the model responds to. The options constructor receives the user-message collection. |
| `Instructions` | Two-sentence explanation instruction or the classifier's fixed rules. | Supplies application instructions separately from user input. Sentence count is guided by the prompt rather than enforced in C#. |
| `MaxOutputTokenCount` | `512` on explanations, streaming, and classification. | Limits output tokens, including reasoning tokens where applicable; this is not a character limit or a total request-cost cap. |
| `StoredOutputEnabled` | `false` on those explicit options. | Disables storing the response for later API retrieval. This setting alone is not a guarantee about all service data retention. |
| `cancellationToken` / positional `ct` | HTTP request token; linked 45-second timeout token in classification. | Allows cancellation of the asynchronous operation. Cancellation does not establish that upstream work incurred no usage. |

Hello uses the short overload and does not explicitly set instructions, output-token limits, or storage behavior. For the full explanation and classifier flows, inspect `ResponseResult.Status` before accepting output. `ResponseStatus.Completed` indicates completion; it does not establish that the answer is correct.

### `ResponseResult.GetOutputText()` — extract generated text

This helper takes no parameters and extracts text from the returned response. It performs no additional API request. [HelloController.cs](Controllers/HelloController.cs) returns that text directly; [OpenAiController.cs](Controllers/OpenAiController.cs) rejects empty explanation text; [TicketClassifier.cs](TicketClassifier.cs) trims the text and validates it against the three allowed labels.

Use it when your endpoint needs a string rather than the full response object. Extraction alone does not validate completion, handle a refusal as a domain result, or enforce an output contract.

`ResponseResult.Usage` is a property, not another SDK call. The code reads these fields for reporting and accounting:

| Field | Meaning in this application |
| --- | --- |
| `InputTokenCount` | Total reported input tokens, including the cached subset. |
| `InputTokenDetails?.CachedTokenCount` | Cached input tokens, charged at the configured cached rate in the local estimate; missing details default to zero. |
| `OutputTokenCount` | Reported output tokens used for output-cost estimation. |
| `OutputTokenDetails?.ReasoningTokenCount` | Reasoning-token subset reported for visibility; missing details default to zero. It is not added again to the output-cost calculation. |

The explanation endpoint can return `usage: null`. Classification stops further generation if usage is missing or unknown. Its estimate is `((input - cached) × inputRate + cached × cachedRate + output × outputRate) / 1,000,000`.

### `ResponsesClient.CreateResponseStreamingAsync(options, ct)` — receive incremental output

Use streaming to display a longer explanation as it arrives. Parameters are the same request options and cancellation token used for buffered explanations, with `StreamingEnabled = true` in the streaming action.

```csharp
await foreach (StreamingResponseUpdate update in
    responses.CreateResponseStreamingAsync(options, ct))
{
    if (update is StreamingResponseOutputTextDeltaUpdate delta)
    {
        await Response.WriteAsync(delta.Delta, ct);
        await Response.Body.FlushAsync(ct);
    }
}
```

The method returns an asynchronous sequence of updates:

- The controller forwards only text fragments from `StreamingResponseOutputTextDeltaUpdate.Delta`.
- `WriteAsync` and `FlushAsync` are ASP.NET Core/.NET methods that send and flush the text.
- The endpoint omits final usage reporting and the buffered endpoint's completion and nonempty-output checks.

Try the existing streaming request with `curl -N` from [Example requests](#example-requests), and compare its incremental plain-text output with the buffered explanation's JSON response. Both requests call the service and can incur usage.

### `ChatClient.CompleteChatAsync(messages, cancellationToken: ct)` — answer role-based messages

Use Chat Completions when integrating a message-list workflow. [OpenAiController.cs](Controllers/OpenAiController.cs) sends:

```csharp
var result = await chat.CompleteChatAsync(
    [new SystemChatMessage("You are a concise support assistant."),
     new UserChatMessage(text)], cancellationToken: ct);
```

The parameters and message constructors serve these roles:

- **`messages`:** conversation supplied for this call.
- **`SystemChatMessage(instruction)`:** builds a local message containing application instructions.
- **`UserChatMessage(text)`:** builds a local message containing user input.
- **`ChatCompletionOptions`:** optional and omitted here; no per-call output-token limit is configured.
- **`cancellationToken`:** propagates HTTP cancellation.

The model comes from `GetChatClient(chatModel)`.

The result wrapper's `Value` contains a `ChatCompletion`. The controller concatenates `reply.Content` text parts and rejects missing or empty text. Each request starts with a fresh two-message list; a conversation across requests would require storing and resending history.

### `EmbeddingClient.GenerateEmbeddingAsync(input, cancellationToken: ct)` and `OpenAIEmbedding.ToFloats()` — obtain a vector

Use embeddings as input to semantic search, document similarity, or clustering. `input` is the validated text string; the optional `EmbeddingGenerationOptions` argument is omitted, and `cancellationToken` propagates HTTP cancellation. The model comes from `GetEmbeddingClient(...)`.

```csharp
OpenAIEmbedding embedding = await embeddings.GenerateEmbeddingAsync(
    text, cancellationToken: ct);
float[] vector = embedding.ToFloats().ToArray();
```

Each step has a separate role:

- **`GenerateEmbeddingAsync(...)`:** calls the service and returns an embedding.
- **`ToFloats()`:** takes no parameters and exposes the vector as `ReadOnlyMemory<float>` locally.
- **`ToArray()`:** converts that memory to an array for the JSON response using .NET.

Storing vectors, comparing them, and retrieving documents remain application work.

### `ModerationClient.ClassifyTextAsync(text, ct)` — obtain a safety classification

Use moderation before accepting or processing user content. The first parameter is the text to classify, and the second is the cancellation token. The model comes from `GetModerationClient(...)`; no additional moderation options are supplied here.

```csharp
ModerationResult result = await moderation.ClassifyTextAsync(text, ct);
bool flagged = result.Flagged;
```

`Flagged` is a result property used in two ways:

- **Moderation endpoint:** returns a `flagged` boolean for the caller to act on.
- **Ticket classifier:** throws `input_moderation` for flagged input, preventing generation.

Try the existing moderation and classification requests in [OpenAiBasicsApi.http](OpenAiBasicsApi.http) to inspect their different response contracts. Its empty-ticket request exercises local validation and returns `400` without making an upstream call.

## Local behavioral evaluations

[Evals.cs](Evals.cs) defines six cases covering billing, a technical issue, an acknowledgement message, prompt injection, empty input, and oversized input. The suite calls the classifier directly and does not use the hosted Evals API.

Reports include:

- Pass rate and cost per passing case.
- Per-case latency and estimated cost.
- Model, prompt, SDK, and dataset versions.

Infrastructure or accounting failures abort the run rather than count as expected guardrail rejections.

Four cases can call upstream services; empty and oversized inputs fail locally. Evaluations incur API usage and share the classifier's attempt and budget limits. These cases provide a small regression baseline, not comprehensive evidence of classification accuracy.

## Running locally

Requirements: **.NET 10 SDK** and an **OpenAI API key** with access to the configured models. Run from the repository root:

```bash
dotnet restore

# Bash: read the key without displaying it or saving it in command history.
read -rsp 'OpenAI API key: ' OPENAI_API_KEY
export OPENAI_API_KEY
printf '\n'

dotnet build --no-restore
dotnet run --launch-profile http
```

The HTTP profile starts in Development at **http://localhost:5164**. The OpenAPI document is available at `/openapi/v1.json` in Development. Automatic reloads are available through `dotnet watch run --launch-profile http`.

Environment variables override JSON configuration:

| Setting | Checked-in value / behavior |
| --- | --- |
| `OPENAI_API_KEY` | Placeholder in `appsettings.json`; supply a credential through the environment or a secret store. |
| `OPENAI_MODEL` | `gpt-6.1-sol` for Responses and classification. |
| `OPENAI_CHAT_MODEL` | Optional; defaults to the main model. Requires a model supporting Chat Completions. |
| `OPENAI_INPUT_USD_PER_MILLION` | `2.00`; uncached input rate for classification estimates. |
| `OPENAI_CACHED_INPUT_USD_PER_MILLION` | `0.10`; cached input rate. |
| `OPENAI_OUTPUT_USD_PER_MILLION` | `10.00`; output rate. |

The rates are configuration examples, not current pricing claims. All three rates must be nonnegative and match the selected model for meaningful estimates; missing or invalid rates prevent startup. Environment-variable rates use decimal points. Never commit a real API key.

If `OPENAI_MODEL` is absent from every configuration source, hello falls back to `gpt-6-luna`; the other Responses endpoints and classifier fall back to `gpt-6.1-sol`. Embeddings use `text-embedding-3-small`, and moderation uses `omni-moderation-latest`.

### Example requests

Classify a support ticket:

```bash
curl -sS http://localhost:5164/api/tickets/classify \
  -H 'Content-Type: application/json' \
  -d '{"text":"I was charged twice."}'
```

A successful response contains a `label` such as `billing` and the request's numeric `estimatedUsd`.

Stream an explanation with curl buffering disabled:

```bash
curl -N http://localhost:5164/api/openai/explanations/stream \
  -H 'Content-Type: application/json' \
  -d '{"text":"Explain async/await."}'
```

Run the local evaluation suite in Development:

```bash
curl -sS -X POST http://localhost:5164/api/evals
```

[OpenAiBasicsApi.http](OpenAiBasicsApi.http) includes additional requests for successful and invalid input paths.

## Code map

| File | Responsibility |
| --- | --- |
| [Program.cs](Program.cs) | Configuration, dependency injection, SDK clients, and exception middleware. |
| [HelloController.cs](Controllers/HelloController.cs) | Minimal Responses API integration. |
| [OpenAiController.cs](Controllers/OpenAiController.cs) | Explanations, streaming, chat, embeddings, and moderation. |
| [TicketsController.cs](Controllers/TicketsController.cs) | Classification and evaluation routes, plus the shared request gate. |
| [TicketClassifier.cs](TicketClassifier.cs) | Classification prompt, guardrails, limits, and usage accounting. |
| [Evals.cs](Evals.cs) | Behavioral cases and evaluation measurements. |
| [OpenAiBasicsApi.http](OpenAiBasicsApi.http) | Manual API requests. |

## Validation and scope

There is no dedicated automated test project. The repository's validation workflow uses `dotnet build --no-restore`, `dotnet format --verify-no-changes`, and manual `.http` requests for successful and invalid paths. Local evaluations provide additional behavioral checks against live upstream services.

The current implementation is a focused integration example. Durable accounting across replicas, persisted conversation history, vector search, and broader regression datasets are possible extensions.
