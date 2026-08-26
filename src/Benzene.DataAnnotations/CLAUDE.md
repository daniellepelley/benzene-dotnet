# Benzene.DataAnnotations

## What this package does
Validates request objects with the BCL `System.ComponentModel.DataAnnotations` attributes
(`[Required]`, `[Range]`, `[StringLength]`, `[RegularExpression]`, custom `ValidationAttribute`s, …)
as Benzene pipeline middleware. For each message it runs `Validator.TryValidateObject(...)` on the
request before the handler runs and short-circuits with a validation-status result carrying the
attributes' error messages. A deliberately minimal alternative to `Benzene.FluentValidation` - no DI
scanning, no per-*rule* status mapping (`.WithStatus(...)`-equivalent), and no schema-builder
integration. It does honour the shared handler-level `[ValidationStatus]` override (see below).

## Key types/interfaces
- `ValidationMiddleware<TRequest, TResponse> : IMiddleware<IMessageHandlerContext<TRequest, TResponse>>`
  (Name `"DataAnnotationValidation"`) - null request → `BenzeneResult.SetFailed<TResponse>(status, "Request is null")`;
  otherwise runs `Validator.TryValidateObject(request, ctx, results, validateAllProperties: true)` and,
  if any `ValidationResult` was produced, sets `BenzeneResult.Set<TResponse>(status, errors)` and
  stops the pipeline. Each `ValidationResult` becomes one `BenzeneError` per entry in its
  `MemberNames` (so a result naming two members yields two errors, same `Message`, different
  `Field`); a result naming no member yields one field-less error. `Code` is always `null` -
  `System.ComponentModel.DataAnnotations.ValidationResult` doesn't expose the attribute that
  produced it. No errors → `next()`. The failure `status` is resolved once, via an optional
  `IValidationStatusMapper` (`Benzene.Abstractions.Validation`) - when one is registered (e.g.
  `Benzene.FluentValidation`'s `DefaultValidationStatusMapper`) it honours `[ValidationStatus]` on
  the resolved handler type; absent a registered mapper, falls back to
  `IDefaultStatuses.ValidationError` (or the built-in literal), same as before this mapper existed.
- `ValidationMiddlewareBuilder : IHandlerMiddlewareBuilder` - constructs the middleware per handler,
  resolving both `IDefaultStatuses` and `IValidationStatusMapper` (both optional via `TryGetService`).
- `DependencyExtensions.UseDataAnnotationsValidation(this IMessageRouterBuilder)` - the single entry
  point; adds `ValidationMiddlewareBuilder` to the pipeline. There is no `AddDataAnnotations` DI step
  and nothing to register, because validation reads attributes off the request type at runtime.

## When to use this package
- When request rules are simple enough to express as attributes and you don't need a schema builder
  or async validation.
- When migrating request models that already carry ASP.NET MVC / Web API DataAnnotations attributes.
- Prefer `Benzene.FluentValidation` when you also want validation rules reflected into the OpenAPI/
  `benzene` spec, or per-*rule* result-status control (`.WithStatus(...)`) rather than per-handler.

## Dependencies on other Benzene packages
- **Benzene.Abstractions.MessageHandlers** - `IMessageHandlerContext<>`, `IMessageRouterBuilder`,
  `IHandlerMiddlewareBuilder`. Transitively brings in `Benzene.Abstractions.Middleware`
  (`IMiddleware<>`) and `Benzene.Results` (`BenzeneResult.ValidationError`).
- **Benzene.Abstractions.Validation** - `IValidationStatusMapper`, the shared status-mapping
  contract; no `IValidationStatusMapper` implementation ships in this package (only
  `Benzene.FluentValidation` provides `DefaultValidationStatusMapper`).
- `System.ComponentModel.DataAnnotations` is part of the BCL - no NuGet package reference.

## Important conventions
- The result's errors are structured `BenzeneError`s (`Message`/`Field`/`Code`), not bare strings -
  `Field` is populated, `Code` is always `null` (see `Benzene.Results/CLAUDE.md`'s capability table).
- Put `ValidationAttribute`s on the request type's properties; the middleware validates all
  properties (`validateAllProperties: true`).
- The result is `ValidationError` unless a registered `IValidationStatusMapper` resolves a different
  status via `[ValidationStatus]` on the handler - this package has no per-*rule* status mapping of
  its own, and ships no mapper implementation (install `Benzene.FluentValidation` for
  `DefaultValidationStatusMapper`, or register a custom `IValidationStatusMapper`).
- Validation is synchronous (`Validator.TryValidateObject`); there is no async validation path.
- A request type with no annotations simply passes (an empty result list), it is not rejected.
