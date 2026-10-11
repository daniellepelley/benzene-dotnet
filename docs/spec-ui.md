# Spec UI

Benzene can serve its own message spec as JSON (see [Spec](spec.md)) — topics, request/response
payloads, broadcast events, and validation rules. **`Benzene.Spec.Ui`** renders that spec in a
browsable, Swagger-UI-style page. It's the Benzene equivalent of `UseSwaggerUI`, but organised
around message **topics** rather than URL paths.

## What it shows

- **Message topics** — each request/response topic as an expandable card, with any HTTP mappings,
  and the request and response payloads rendered as property tables.
- **Broadcast events** — fire-and-forget topics and their message payloads.
- **Schemas** — the full `components.schemas` catalogue.
- **Validation rules** — required fields, `format` (e.g. `uuid`, `email`), `enum` values, string
  length and numeric range, `pattern`, and `nullable` — shown as inline constraint chips, sourced
  from the same validation metadata the `benzene` spec projects onto each schema.
- **Example payloads** — the generated `example` the `benzene` spec carries per topic and event
  (see [Spec](spec.md)), pretty-printed with a one-click **copy** button, ready to paste into a
  test invocation.
- **Try it** — when the spec advertises a message endpoint (`messageEndpoint`, exposed by
  `UseBenzeneMessage` — see [Payload Testing](payload-testing.md)), every topic and event card
  gains an editable payload (pre-filled from the example), a headers editor, and a
  **Send**/**Dispatch** button. The response envelope renders inline: HTTP status (200 for every
  processed envelope) and Benzene status chips, response headers, pretty-printed body, and round-trip time. Without a
  `messageEndpoint` in the spec the page stays a read-only viewer — capability is gated by the
  service, not the page.

## Serving it

`UseSpecUi` is a transport-agnostic Benzene HTTP middleware — it works on **any** HTTP pipeline
(AWS Lambda API Gateway, Azure Functions, ASP.NET Core, or the self-host server), which matters
because most Benzene services run serverless. It has no ASP.NET dependency; it writes the page
straight through Benzene's response adapter with `text/html`.

Add it to your HTTP pipeline, before the message handlers, alongside a `spec` endpoint:

```csharp
using Benzene.Schema.OpenApi;   // UseSpec
using Benzene.Spec.Ui;          // UseSpecUi

// AWS Lambda API Gateway (the same call works on Azure Functions, ASP.NET, self-host).
app.UseApiGateway(http => http
    .UseSpecUi()                                   // serves GET /benzene/spec-ui
    .UseSpec()                                     // the benzene:spec topic; map GET /benzene/spec to it (see docs/spec.md)
    .UseMessageHandlers(router => router.UseFluentValidation())
);

// Customise the path and/or the spec URL it fetches:
app.UseApiGateway(http => http
    .UseSpecUi("/docs", "/api/spec?type=benzene")
    .UseSpec()
    .UseMessageHandlers(router => router)
);
```

Browse to `/benzene/spec-ui`. The page fetches `/benzene/spec?type=benzene` (`SpecUiExtensions.DefaultSpecUrl`)
and renders it. Both are the [default paths](https://github.com/daniellepelley/Benzene/blob/main/docs/specification/transport-bindings.md#1-the-binding-contract)
every Benzene port uses, and both are configurable.

> Use the **`benzene`** spec type — the UI is designed around its topic/payload/validation shape,
> not `openapi`/`asyncapi`.

## Serving it yourself

The viewer is a single self-contained HTML file with no external requests. If you aren't using the
middleware, get the markup from `SpecUiPage` and write it to any HTTP response:

```csharp
var html = SpecUiPage.GetHtml("/benzene/spec?type=benzene"); // inject the spec URL onto the page
// write `html` with content-type "text/html"
```

## Loading a spec without wiring

Open the page standalone and either append `?url=<spec-json-url>`, or use the **Load spec** button
to paste spec JSON directly. With no spec supplied it renders a built-in sample so you can see the
layout.
