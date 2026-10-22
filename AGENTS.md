# Repository Guidelines

## Project Structure & Module Organization

This is a controller-based ASP.NET Core Web API targeting .NET 10, using OpenAI .NET SDK 2.14.0.

- `Program.cs`: configuration, dependency injection, middleware, and endpoint registration.
- `Controllers/`: hello, OpenAI feature, and ticket/evaluation endpoints. `RequestGateFilter` shares the ticket controller file.
- `TicketClassifier.cs`: classification, guardrails, usage accounting, and token rates.
- `Evals.cs`: local evaluation cases and grading; it does not call the hosted Evals API.
- `OpenAiBasicsApi.http`: manual requests for each endpoint.
- `Properties/launchSettings.json` and `appsettings*.json`: launch profiles and configuration.

There is no dedicated test project or frontend asset directory. Treat `bin/` and `obj/` as generated output.

## Build, Test, and Development Commands

Run commands from the repository root with the .NET 10 SDK:

- `dotnet restore`: restore NuGet dependencies.
- `dotnet build --no-restore`: compile after restoring.
- `dotnet run --launch-profile http`: start in Development at `http://localhost:5164`.
- `dotnet watch run --launch-profile http`: run with automatic reloads.
- `dotnet format --verify-no-changes`: check formatting; no custom formatter or linter configuration is present.

## Coding Style & Naming Conventions

Use four-space indentation, file-scoped namespaces, nullable annotations, and PascalCase for types and public members. Match existing camelCase locals and fields. Root types use `OpenAiBasicsApi`; controllers use `OpenAiBasicsApi.Controllers`. Keep top-level statements in `Program.cs`. Preserve cancellation-token propagation and reuse registered SDK clients. Update `.http` examples when routes or request bodies change.

## Testing Guidelines

No automated test framework or coverage threshold is configured. Build changes and exercise affected `.http` requests. Check invalid input as well as successful responses. `POST /api/evals` runs the local suite in Development, incurs API usage, and shares the classifier's 12-attempt and USD 0.05 soft limits. Preserve `RequestGateFilter` serialization when changing accounting.

## Commit & Pull Request Guidelines

Git history is unavailable in this checkout, so no existing message convention could be verified. Use concise, imperative subjects such as `Fix ticket filter registration`. PRs should describe behavior changes, relevant issues, configuration requirements, and validation performed. Include request/response examples for API changes.

## Security & Configuration Tips

Supply `OPENAI_API_KEY` through hidden terminal input or a secret store; never commit literal keys. Read settings through `IConfiguration`; environment variables can override JSON values. Configure the three `OPENAI_*_USD_PER_MILLION` rates with nonnegative values. Avoid logging secrets or raw SDK exception bodies. Keep unknown-usage safeguards intact; local accounting is not a guaranteed billing cap.
