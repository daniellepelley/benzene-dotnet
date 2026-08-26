# Benzene.Abstractions.Validation

## What this package does
Defines validation **schema** abstractions for Benzene. This is a schema-description model, not a
FluentValidation-style `IValidator<T>` runner: types here *describe* the validation rules that apply
to a request's properties (name/description, plus rule-specific metadata such as a min length or a
regex), and map a validation outcome to a Benzene result status. Concrete validation execution and
the rule-to-schema wiring live in the implementation/adapter packages that consume these interfaces.

## Key types/interfaces
- `IValidationSchema` - base of every rule schema: `Name` + `Description`. Rule-specific schemas
  extend it with their parameters:
  - `IMinLengthValidationSchema` (`Min`), `IMaxLengthValidationSchema` (`Max`)
  - `IIsOneOfValidationSchema` (`Options`)
  - `IRegexValidationSchema` (`Expression`)
- `IValidationSchemaBuilder` - `GetValidationSchemas(Type)` returns, per property name, the array of
  `IValidationSchema` rules that apply to that request type.
- `IValidationStatusMapper` - `GetStatus(handlerType, requestType, result)` maps a validation
  outcome to a Benzene result status string.
- `ValidationStatusAttribute` - class-only attribute, applied to a message handler type, overriding
  the result status a failed validation produces for that handler (e.g. `BadRequest` vs
  `ValidationError`).
- `ValidationConstants` - the canonical rule-name strings (`MinLength`, `MaxLength`, `IsGuid`,
  `IsNumeric`, `NotEmpty`, `NotNull`, `IsOneOf`, `Regex`, `Email`, `Phone`, `GreaterThan`, ... ).

## When to use this package
- When implementing a validation provider/adapter that exposes its rules as Benzene schemas.
- As a dependency for validation middleware.
- Rarely referenced by application code directly.

## Dependencies on other Benzene packages
- **Benzene.Abstractions** - Core abstractions.

## Important conventions
- Schemas *describe* rules; they do not execute validation themselves.
- Rule names come from `ValidationConstants` (case-sensitive).
- A failed validation is surfaced as a Benzene result status via `IValidationStatusMapper` /
  `ValidationStatusAttribute`, so validation failures flow through the same result pipeline as
  handler outcomes. All three validation adapters (`Benzene.FluentValidation`,
  `Benzene.DataAnnotations`, `Benzene.JsonSchema`) resolve an optional `IValidationStatusMapper` and
  honour it identically - only `Benzene.FluentValidation` ships a default implementation
  (`DefaultValidationStatusMapper`); register it (or your own `IValidationStatusMapper`) once and
  every adapter honours `[ValidationStatus]` the same way. Absent a registered mapper, each adapter
  keeps its own pre-existing default validation-error status unchanged.

## Tests
Exercised via the validation middleware/implementation tests in `test/` (this package is
interfaces + constants only).
