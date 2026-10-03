# Purchased-data Email Validation

## Decision

Email Validation remains its own independently deployed API at
`https://email.digitalwarehouse.io`. The customer-facing Search and Match & Append API remains at
`https://services.digitalwarehouse.io`. No validation engine, provider policy, retry logic, queue,
outbox, or persistence model is copied into OpenMeta.

The integration endpoint is:

```http
POST /v1/purchased-results/{transactionId}/email-validation
Authorization: Bearer <customer-api token>
Idempotency-Key: <stable retry key>
Content-Type: application/json

{
  "emailColumn": "Email",
  "enableSmtp": true
}
```

It returns `202 Accepted` with the existing Email Validation job resource and a `Location` header
for `/v1/email-validation-jobs/{jobId}`.

## Trust and authorization path

```text
Customer API client
        |
        | emailvalidation.jobs.write + Search/Match read scope
        v
Email Validation API
        |
        | forwards the caller bearer token; no asserted owner/account headers
        v
services.digitalwarehouse.io/v1/transactions/{id}/results
        |
        | active API client + active account + exact client/account owner
        | completed Search or Match & Append + durable output + settled purchase
        v
short-lived owner-bound download descriptor
        |
        v
Email Validation extracts only the requested column
        |
        v
existing durable validation job + outbox + Service Bus worker
```

OpenMeta is authoritative for purchase provenance. A public Search/Match result becomes visible only
after durable output finalization and credit consumption. Its result lookup binds the transaction to
the active API client and account. Email Validation accepts the descriptor only when it points to the
configured customer API origin and expected transaction download path; redirects are disabled.

Field authorization comes from the actual purchased output. If the named field is absent, ambiguous,
or empty, no job is created. Email Validation never accepts a client-provided email list on this
route, never reads an unpurchased internal field, and never treats knowledge of a transaction ID as
authorization.

## Responsibilities

OpenMeta owns API-client/account activation, Search/Match permissions, dataset and field policy,
purchase completion, credit settlement, result ownership, and signed result download authorization.

Email Validation owns the public adapter for this capability, bounded column extraction, job
idempotency and ownership, validation-job persistence, its transactional outbox, Service Bus
dispatch, processing, retries/revalidation, and customer-safe results.

The edge (Nginx or a future managed gateway/YARP) may route and protect traffic, but it does not make
purchase or field-eligibility decisions. A second proxy is not required for this integration because
each API retains its own stable hostname and deployment pipeline.

## Administrative and existing web behavior

Interactive web users keep the existing single-address and raw bulk adapters. Machine-to-machine
tokens are denied on those arbitrary-address routes unless explicitly authorized with
`emailvalidation.admin`. Administrators may therefore perform support/operational validation without
weakening the purchased-data rule for standard API clients.

## Error contract

| Status | Code | Meaning / remediation |
| --- | --- | --- |
| `202` | — | Durable Email Validation job accepted; follow `Location`. |
| `400` | validation problem | Correct the transaction identifier, column name, or idempotency key. |
| `401` | authentication problem | Obtain a valid bearer token for an accepted audience. |
| `403` | `EMAIL_VALIDATION_NOT_AUTHORIZED` | Grant `emailvalidation.jobs.write` and the applicable result-read permission. |
| `404` | `PURCHASED_RESULT_NOT_FOUND` | The result is absent or not owned by this exact API client; ownership is intentionally not disclosed. |
| `409` | `PURCHASE_NOT_COMPLETED` | Wait for the Search or Match & Append transaction to complete. |
| `409` | `EMAIL_VALIDATION_ALREADY_RUNNING` / `EMAIL_VALIDATION_ALREADY_COMPLETED` | Follow the existing job instead of creating duplicate work. |
| `413` | `PURCHASED_RESULT_TOO_LARGE` | Split the purchase/job within the configured operational limit. |
| `422` | `EMAIL_NOT_INCLUDED_IN_PURCHASE` | Select an email column that is present in the purchased output. |
| `429` | rate-limit problem | Back off and retry. |
| `502` / `503` | dependency problem | Retry with the same idempotency key. |

## Operations still required

Before enabling this route for a production customer:

1. Add `https://services.digitalwarehouse.io` to the Email Validation API's accepted audiences.
2. Add `emailvalidation.jobs.write` and `emailvalidation.jobs.read` to the customer API Auth0
   resource server and assign them to the intended M2M client; retain the applicable `search:read`
   and/or `match:read` permission.
3. Verify `Api:OpenMeta:PublicApiBaseUrl=https://services.digitalwarehouse.io` in production.
4. Run authenticated Search and Match & Append purchases through submission, validation-job polling,
   result paging, idempotent retry, cross-account denial, and dependency-failure recovery.
5. Add production dashboards/alerts for purchased-result authorization failures, dependency latency,
   job outbox age, processing failures, and rate limits without logging raw addresses.
