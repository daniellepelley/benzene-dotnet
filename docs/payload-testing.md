# Payload Testing

AWS ships a Lambda Test Tool for firing test payloads at a locally running Lambda; Swagger UI lets
you construct and send test requests at an HTTP API. Benzene's equivalent is **topic-centric**:
construct a payload for a *topic*, and send it into the running service through the same pipeline
every transport uses — routing, validation, middleware, handler — regardless of whether that topic
also has an HTTP mapping or is normally fed by SQS, SNS, EventBridge, Kafka, or Event Hubs.

Three pieces work together:

1. **Example payloads in the spec** — the `benzene` spec carries a generated, validation-aware
   `example` for every topic and event (see [Spec](spec.md)).
2. **The message endpoint** (`UseBenzeneMessage` on any HTTP pipeline, this page) — accepts a
   POSTed BenzeneMessage envelope and dispatches it into the message pipeline.
3. **The [Spec UI](spec-ui.md)** — renders the examples and, when the service exposes a message
   endpoint, offers a *Try it* panel to edit and send payloads from the browser.

## The message endpoint

`UseBenzeneMessage` (in `Benzene.Http`, namespace `Benzene.Http.BenzeneMessage`) is the HTTP
equivalent of the direct AWS Lambda invoke path — same name, same overload shapes. It works on
**any** Benzene HTTP transport (AWS Lambda API Gateway, Azure Functions, ASP.NET Core, self-host)
because it drives the transport-neutral request/response adapters directly.

```csharp
using Benzene.Http.BenzeneMessage;

// Inline pipeline — serves POST /benzene-message
app.UseApiGateway(apiGateway => apiGateway
    .UseBenzeneMessage(messageApp => messageApp
        .UseMessageHandlers(router => router.UseFluentValidation()))
    .UseMessageHandlers(router => router.UseFluentValidation())
);

// Or share one message pipeline across adapters (direct Lambda invoke + HTTP):
var benzeneMessagePipeline = aws.Create<BenzeneMessageContext>()
    .UseMessageHandlers(router => router.UseFluentValidation());

aws.UseBenzeneMessage(benzeneMessagePipeline);                       // direct Lambda invoke
aws.UseApiGateway(apiGateway => apiGateway
    .UseBenzeneMessage(benzeneMessagePipeline)                       // HTTP endpoint
    .UseMessageHandlers());
```

### The wire contract

POST a BenzeneMessage envelope; the `body` is the payload **as a JSON string**:

```bash
curl -X POST http://localhost:8080/benzene-message \
  -H "content-type: application/json" \
  -d '{ "topic": "order:create", "headers": {}, "body": "{\"customerId\":\"11111111-1111-1111-1111-111111111111\"}" }'
```

The response is the response envelope, and the HTTP status is **200 for every envelope the endpoint
processed**, whatever the outcome — `ok`, `validation-error`, `not-found`, `deleted` alike. The envelope
carries the outcome (`statusCode`, `isSuccessful`); read it, not the status line:

```json
{ "statusCode": "ok", "isSuccessful": true, "headers": { }, "body": "{\"orderId\":\"...\"}" }
```

A failure's `body` is the problem document (RFC 9457), without the HTTP `status` member — it isn't an
HTTP response itself. The envelope is always plain JSON (camelCase), independent of whatever payload
formats the app's own serializer negotiates. An envelope with no topic is processed too (the router
answers `validation-error`, still 200). Only a request body that isn't an envelope at all — empty, not
JSON, not a JSON object — is a carrier failure: **400**, with an `application/problem+json` body.
401/403 come only from authentication your host enforces before the envelope is read; an `unauthorized`
from Benzene auth middleware in the pipeline is a processed envelope (200).

### Options

```csharp
apiGateway.UseBenzeneMessage(new BenzeneMessageHttpOptions
{
    Path = "/admin/benzene-message",              // default: /benzene-message
    TopicFilter = topic => topic.StartsWith("order:")  // reject → a not-found envelope (HTTP 200)
}, messageApp => messageApp.UseMessageHandlers());
```

### Spec advertisement

Registering the endpoint also registers an `IBenzeneMessageHttpEndpointInfo`, and the `benzene`
spec advertises it as a top-level `messageEndpoint` field. Consumers — the Spec UI's *Try it*
panel in particular — feature-detect send capability on that field: no field, no send button.

## Generating test payload files (AWS Lambda Test Tool)

For firing payloads from tooling rather than the browser, the `benzene` CLI can generate
per-topic test payload files from a service's spec — one JSON file per topic per transport
(`<topic>-benzene-message.json`, `<topic>-sns.json`, `<topic>-sqs.json`, and
`<topic>-api-gateway.json` for HTTP-mapped topics), ready to paste into the AWS Lambda Test Tool
or send with `curl`:

```bash
# from a running lambda
benzene lambda-test-tool -profile my-aws-profile -lambda-name my-service -directory ./test-payloads

# or from a spec JSON file on disk
benzene lambda-test-tool -file ./spec.json -directory ./test-payloads
```

The generation lives in `Benzene.CodeGen.LambdaTestTool` (`LambdaTestFilesBuilder` +
`DefaultExampleBuilders`), and the payload bodies come from the same spec-driven example
generator as everything else on this page.

## ⚠ Security

The endpoint dispatches **every topic the pipeline routes**, including topics that have no HTTP
mapping and would otherwise only be reachable from a queue or an event bus. Treat it accordingly:

- It is **opt-in only** — nothing registers it implicitly.
- Intended for **local development** and protected/admin environments.
- Restrict what it can reach with `TopicFilter` — the topic restriction the spec requires every envelope
  endpoint to support (a rejected topic is answered `not-found`, exactly as if no handler matched) — and
  compose your authentication middleware **before** it in the pipeline: the endpoint runs the same pipeline,
  and so the same auth, as your other transports.
- Do **not** expose it unauthenticated in production.
