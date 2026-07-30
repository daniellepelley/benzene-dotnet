# Benzene.Aws.Lambda.Hosting

## What this package does
Runs a Benzene AWS Lambda entry point in the custom-runtime bootstrap loop, so a pure-Benzene
function's `Program.cs` is one line:

```csharp
await AwsLambdaBootstrap.RunAsync<StartUp>();
```

`.NET` has had no managed Lambda runtime since .NET 8, so a Benzene function ships as a self-contained
executable on `provided.al2023` and pumps invocations itself. That is the same three lines of
`HandlerWrapper` + `LambdaBootstrap` boilerplate in every function; this package owns it.

## Why it is its own package (ASP.NET-free)
It references `Amazon.Lambda.RuntimeSupport` and `Benzene.Aws.Lambda.Core`, and **nothing else** — no
ASP.NET Core, no `Amazon.Lambda.AspNetCoreServer`. A function that serves only queues, streams, and
events (or Benzene's own API Gateway binding) takes this and stays light. The ASP.NET adapter is a
separate package, `Benzene.Aws.Lambda.AspNet`, which references this one and drives the same loop from
inside `app.Run()`.

## Key types
- `AwsLambdaBootstrap.RunAsync<TStartUp>()` — host a `BenzeneStartUp` and run the loop. Builds an
  `AwsLambdaHost<TStartUp>` (which does the WarmUp + start-up checks) and owns its disposal.
- `AwsLambdaBootstrap.RunAsync(IAwsLambdaEntryPoint)` — run an entry point the caller built (e.g. a
  custom `AwsLambdaHost<TStartUp>` subclass that overrides `OnInvocationCompleteAsync` to flush
  telemetry). The caller owns its lifetime.
- `AwsLambdaBootstrap.RunAsync(IAwsEntryPointBuilder)` — build from a builder, then run.

## When to use this package
- Any pure-Benzene Lambda: SQS/SNS/EventBridge/Kinesis/DynamoDb/Kafka consumers, or an HTTP function on
  Benzene's own `Benzene.Aws.Lambda.ApiGateway` binding.
- **Not** when ASP.NET Core owns the HTTP front door of a mixed function — use
  `Benzene.Aws.Lambda.AspNet` there (it references this).

## Dependencies on other Benzene packages
- **Benzene.Aws.Lambda.Core** — `AwsLambdaHost<TStartUp>`, `AwsLambdaEntryPoint`, `IAwsLambdaEntryPoint`.

## Important conventions
- The entry point deals in `Stream` in / `Stream` out, so the handler wrapper needs no serializer — the
  pipeline's bindings deserialize each event to its own type.
- The overloads that *build* the entry point dispose it; the overload that *receives* one does not.
