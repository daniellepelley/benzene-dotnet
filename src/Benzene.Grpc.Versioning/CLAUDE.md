# Benzene.Grpc.Versioning

Request-side transparent payload-version casting for the **gRPC** transport. This is the gRPC-specific
counterpart to `Benzene.Core.Versioning`'s `AddPayloadVersioning`, closing the gap noted in
`docs/specification/versioning.md` §4.2.1: gRPC's request mapper bridges protobuf, so it could not be
wrapped by the generic casting decorator, which wraps the framework-default serializer mapper.

## Shape

- `IBenzeneServiceContainer.AddGrpcPayloadVersioning(configure)` — declare versioned topics and their
  casters through the same `PayloadVersioningBuilder` as `AddPayloadVersioning` (same eager validation,
  same auto-derived field-drop downcasts). The gRPC context is wired for you; **do not** also call
  `ForContext<GrpcContext>()`.

## How it works

- `AddPayloadVersioning(... ForContext<GrpcContext>())` registers the casters (`ISchemaCasters`), the
  version/topic getters, and a request decorator over the *default* mapper.
- `UsePayloadVersionRequestCasting<GrpcContext, GrpcRequestMapper>()` (added to `Benzene.Core.Versioning`)
  then re-points the request side at the real `GrpcRequestMapper` (last registration wins). The decorator
  reads the wire body as the *incoming* version's CLR shape — still through `GrpcRequestMapper`, so
  protobuf→JSON→POCO bridging runs — then upcasts it into the handler's declared request type.

## Notes

- **Request side only.** gRPC writes its response straight to protobuf via its result setter and has no
  `IResponsePayloadMapper<GrpcContext>` to downcast. If an older *response* shape must be returned over
  gRPC, model it as a distinct topic/method rather than a cast. (Serializer-based transports still cast
  both directions via `AddPayloadVersioning`.)
- A topic with no registered casters, or a message signalling no version, delegates straight through to
  `GrpcRequestMapper` with zero overhead — enabling versioning never changes an unversioned call.
- No third-party dependencies of its own beyond what `Benzene.Grpc` and `Benzene.Core.Versioning` already
  pull in. Keep it to the one entry point; the casting mechanics live in `Benzene.Core.Versioning`.
