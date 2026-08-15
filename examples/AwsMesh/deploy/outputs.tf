output "mesh_ui_url" {
  description = "Open this in a browser to see the Mesh UI (the catalog of discovered services). Requires logging in with an allowlisted Google account first (see mesh_oauth_callback_url below) - the mesh's entire HTTP surface is gated."
  value       = "${aws_apigatewayv2_api.mesh.api_endpoint}/mesh-ui"
}

output "mesh_oauth_callback_url" {
  description = "REQUIRED MANUAL STEP after the first apply: copy this exact URL into the Google OAuth Client's 'Authorized redirect URIs' in Google Cloud Console (APIs & Services -> Credentials -> your OAuth 2.0 Client ID). The API Gateway endpoint is only known after apply, so this can't be pre-registered - until it's added, every login attempt fails at Google with a redirect_uri_mismatch error. See examples/AwsMesh/README.md's 'Auth setup' section."
  value       = "${aws_apigatewayv2_api.mesh.api_endpoint}/mesh/auth/callback"
}

output "mesh_manifest_url" {
  description = "The catalog manifest the Mesh UI reads (handy for curl-ing the raw output)."
  value       = "${aws_apigatewayv2_api.mesh.api_endpoint}/manifest.json"
}

output "mesh_refresh_url" {
  description = "POST here to trigger a discovery + aggregation pass on demand (instead of waiting for the schedule)."
  value       = "${aws_apigatewayv2_api.mesh.api_endpoint}/mesh/refresh"
}

output "service_spec_ui_urls" {
  description = "Each service's Spec UI — open in a browser to explore its contract."
  value       = { for k, api in aws_apigatewayv2_api.service : k => "${api.api_endpoint}/benzene/spec-ui" }
}

output "service_health_urls" {
  description = "Each service's aggregated health endpoint."
  value       = { for k, api in aws_apigatewayv2_api.service : k => "${api.api_endpoint}/benzene/health" }
}

output "artifact_bucket" {
  description = "The S3 bucket holding the discovered registry.json and the generated catalog artifacts."
  value       = aws_s3_bucket.artifacts.id
}

output "orders_outbox_table_name" {
  description = "The orders-api outbox table (work/outbox-plan.md Phase 3) — inspect it (or the CloudWatch logs of orders_outbox_stream / orders_outbox_sweep) to watch envelopes go Pending -> Dispatched, or Parked after MaxAttempts."
  value       = aws_dynamodb_table.orders_outbox.name
}

output "claim_check_bucket" {
  description = "The S3 bucket Benzene.ClaimCheck.Aws.S3 offloads oversized payments:capture payloads to (work/claim-check-plan.md Phase 6) — list objects under the claim-checks/ prefix (dated, one per offload) to see the pattern trigger on a POST /orders with a large SupportingDocument; the aws_s3_bucket_lifecycle_configuration expires them after 14 days regardless of whether they were ever consumed."
  value       = aws_s3_bucket.claim_checks.id
}
