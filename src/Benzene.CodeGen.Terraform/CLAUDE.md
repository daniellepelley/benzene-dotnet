# Benzene.CodeGen.Terraform

## ⚠️ Release status: NOT part of the 1.0 release (`IsPackable=false`)
This package is deliberately excluded from the NuGet release (see the csproj comment). It is at a
fork in the road: either it grows into a complete, opinionated infra generator that covers a real
deployment end-to-end, or it stays a separate/experimental artifact on its own lifecycle. Until
that call is made it must not ship as a full-release package. It stays in the solution so it keeps
compiling and its tests keep running.

The previous **project-specific AWS coupling has been removed** — the `DarwinTopicNamer`
`platform-eventbus_*` allowlist is gone (plain underscore-casing now), the VPC subnet source and the
SNS remote-state name are configurable (`TerraformLambdaSettings.SubnetIdsExpression`,
`TerraformLambdaEventBusPermissionsSettings.SnsRemoteStateName`), and the `AutoTag_*` ignore_changes
entries are now just a generic default (`TerraformLambdaSettings.IgnoredChanges`). The default output
is company-free: no `vpc_config` unless a subnet expression is set, and generic ignore_changes.
New deployment-specific values belong in the settings objects, never as string literals.

## What this package does
Generates Terraform (`.tf`) configuration for AWS Lambda functions hosting Benzene services, so
the infrastructure that delivers events to a service is derived from the service's code instead
of hand-maintained. See `docs/terraform.md`.

## Key types/interfaces
- `TerraformLambdaBuilder : ICodeBuilder<TerraformLambdaSettings>` — the entry point: generates
  `aws_lambda_function` + `aws_iam_role`, and composes the event-source builders below when
  their settings are present (`TopicsMap` → SNS, `EventBridge` → EventBridge).
- `TerraformLambdaEventBusPermissionsBuilder` — SNS-side wiring: `aws_lambda_permission` +
  `aws_sns_topic_subscription` with a `filter_policy` over the Benzene message topics.
- `TerraformEventBridgeRuleBuilder : ICodeBuilder<TerraformEventBridgeRuleSettings>` —
  EventBridge-side wiring: one `aws_cloudwatch_event_rule` whose `event_pattern` matches
  `detail-type` against the service's message topics verbatim (the same contract
  `Benzene.Aws.Lambda.EventBridge` routes on), plus `aws_cloudwatch_event_target` and
  `aws_lambda_permission` (file `aws_lambda_permission_eventbridge.tf` — deliberately distinct
  from the SNS builder's `aws_lambda_permission.tf` so both can coexist). Optional
  `EventBusName` (default bus when null) and `Sources` filter.
- `TerraformEventBridgeRuleBuilderExtensions` — `BuildCodeFiles(settings, ...)` overloads that
  discover the topics from `[Message]` handlers via `ReflectionMessageHandlersFinder`
  (`IMessageHandlerDefinition[]`, `params Type[]`, or `params Assembly[]`), de-duplicated on
  `Topic.Id` (handler versions never reach the wire) and ordinal-sorted for diff-stable output.
- `TerraformDirectoryMerger` — merges generated files into an existing Terraform directory.
- `HclLiteral` — escapes a settings-derived value (`Name`/`Domain`/`SubDomain`/`EntryPoint`/`Runtime`,
  an event-bus/rule name, and especially a Benzene message topic) for safe embedding as an HCL
  string literal: escapes `"` and `\`, and — the sharpest edge of round 14-15's #212/#263 finding —
  neutralizes `${`/`%{` (HCL's own live template-interpolation/directive syntax) via HCL's own
  `$${`/`%%{` escaping convention. An unescaped topic containing `${aws_iam_role.admin.arn}` isn't
  just mangled output — Terraform *evaluates* it as a real expression referencing whatever symbol
  happens to be in scope. Applied everywhere any of the three builders below interpolates a
  settings-derived string as an HCL string literal (not to a Terraform resource label/identifier,
  and not to a deliberately-raw expression like `vpc_config`'s `subnet_ids` or the generator's own
  `"${path.module}/file.zip"` filename literal — both stay untouched, on purpose).

## Important conventions
- Builders emit `ICodeFile[]` (one file per resource type) via `LineWriter(2)` — two-space
  indent, `StartIndent()` blocks.
- Terraform resource labels are `NameFormatter.UnderScoreCase(lambdaName)`; generated resources
  reference the co-generated Lambda as `aws_lambda_function.<label>` — the builders assume they
  run alongside `TerraformLambdaBuilder`.
- An EventBridge rule with no topics throws (`ArgumentException`) rather than emitting a
  match-nothing pattern.

## Dependencies on other Benzene packages
- **Benzene.CodeGen.Core** — `ICodeBuilder`, `CodeFile`, `LineWriter`
- **Benzene.Core.MessageHandlers** — `ReflectionMessageHandlersFinder` for `[Message]` topic
  discovery

## Tests
- `test/Benzene.Core.Test/Autogen/CodeGen/Terraform/TerraformLambdaEventBusPermissionsBuilderTest.cs` —
  `BuildPermission`/`BuildSubscription` (single-resource output, `filter_policy` shape),
  `BuildPermissions`/`BuildSubscriptions` (one resource block per `TopicsMap` entry),
  `BuildCodeFiles`'s two-file output, `NameFormatter.UnderScoreCase`, and the configurable
  `SnsRemoteStateName` in the SNS ARN reference.
- `HclLiteralTest.cs` — the helper directly: plain values, embedded `"`/`\`, and the `${`/`%{`
  neutralization (asserting the live-interpolation form never survives into the output).
  `TerraformLambdaBuilderTest.cs`/`TerraformEventBridgeRuleBuilderTest.cs`/
  `TerraformLambdaEventBusPermissionsBuilderTest.cs` each carry an adversarial-content case
  (quote/backslash/`${`) proving the corresponding builder's `.tf` output stays syntactically valid
  and that no live `"${` interpolation sequence reaches the generated file (#212/#263).
- `test/Benzene.Core.Test/Autogen/CodeGen/Terraform/TerraformDirectoryMergerTest.cs` — real
  filesystem round trips (temp directory, cleaned up via `IDisposable`): no existing file → new
  content passes through unchanged; an existing file whose first line matches the new content's
  first line → that resource block is replaced; no matching first line → the new content is
  appended after the existing file's content; multiple files in one `Merge` call are merged
  independently. Previously zero coverage anywhere in the repo — also fixed a real bug this pass
  surfaced: `Merge` built the existing-file path with a hardcoded `\` separator, which is not a
  path separator on Linux/macOS (a `File.Exists` check for `<dir>\<name>` never matches on those
  platforms, so every merge silently took the "file doesn't exist" branch and clobbered existing
  Terraform files instead of merging into them) — fixed to `Path.Combine(directoryPath, pair.Key)`.
