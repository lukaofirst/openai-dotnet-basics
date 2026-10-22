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

Chat constructs a fresh message list for each request, with no persisted conversation history. Embeddings return vectors without implementing storage or similarity search. Moderation returns a safety signal; the ticket classifier demonstrates how that signal becomes an application decision.

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

Accounting is held in memory, resets on restart, and is not coordinated across replicas. Restarting does not reconcile unknown upstream usage. The limits apply only to classification and its evaluations; other feature endpoints are outside this accounting. A request can exceed the soft budget, so the estimate is not a guaranteed billing cap.

Middleware maps failures to HTTP problem responses without exposing raw SDK exception bodies. Invalid ticket input returns `400`, flagged input returns `422`, rejected output or upstream failures can return `502`, unavailable accounting or limits return `503`, and classification timeouts return `504`.

## Local behavioral evaluations

[Evals.cs](Evals.cs) defines six cases covering billing, a technical issue, an acknowledgement message, prompt injection, empty input, and oversized input. The suite calls the classifier directly and does not use the hosted Evals API.

Reports include pass rate, per-case latency and estimated cost, cost per passing case, and model, prompt, SDK, and dataset versions. Infrastructure or accounting failures abort the run instead of counting as expected guardrail rejections.

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
