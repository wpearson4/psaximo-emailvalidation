# Durable automatic revalidation

## Gap analysis

The repository was inspected before implementation. `EmailValidation.Core` is the existing combined domain/application
boundary; splitting it into new projects would duplicate abstractions without changing dependency direction. It stays
host-neutral and references neither MongoDB nor Azure Service Bus SDKs.

| Capability | Classification before this change | Reused or added |
| --- | --- | --- |
| Validation service | Implemented under `IEmailValidator` / `IEmailValidationExecutor` | `IEmailValidationService` names the existing `IntelligenceEmailValidator`; the live engine was not copied |
| Persistent reuse | Implemented under `IValidationResultReusePolicy` | Reused by every worker retry through the intelligence validator |
| Single-flight | Already implemented correctly | Reused by every worker retry |
| Domain/catch-all intelligence | Already implemented correctly | Reused by the normal validation planner |
| Provider policy/backoff | Already implemented as `IProviderPolicyResolver` and `IDomainBackoffPolicy` | Reused by the generic scheduling policy |
| Provider/local cooldown | Implemented but only enforced while acquiring SMTP leases | Availability now also exposes active domain/provider pacing and cooldown to the retry processor |
| Mongo intelligence | Already implemented for domain/mailbox intelligence | A focused lifecycle collection was added because retry/outbox state does not belong to general intelligence |
| Provisional/final state, stable ID, attempts | Missing | Added additively to result and lifecycle models |
| Retry decision and schedule policy | Missing | Added as separate focused services |
| Durable scheduler / Service Bus SDK | Missing in this solution | Added behind an application contract; default queue `email-validation-retry` |
| Worker host | Missing | Added as a thin Service Bus adapter plus outbox publisher |
| Idempotency / stale retry protection | Missing | Added with deterministic message IDs and Mongo compare-and-set versions |
| Dual-write recovery | Missing | Added as a lifecycle-embedded outbox because Mongo commit plus broker send had a real consistency gap |
| App Configuration / Key Vault | Already implemented correctly for `EmailValidation:*` | Extended with an optional local Service Bus secret override; production patterns are unchanged |

No existing Service Bus namespace, queue name, or reusable messaging implementation was present in this repository.
The queue name is configurable and provisioning is off by default. This avoids assuming management permission or
creating infrastructure in an unknown namespace. Explicit provisioning checks for the queue and creates only a missing
queue; it never deletes or recreates one.

## Dependency boundaries

```text
EmailValidation.Core (domain + application contracts/use cases)
                    ↑
EmailValidation.Infrastructure (Mongo + Azure Service Bus adapters)
                    ↑
EmailValidation.Console / EmailValidation.Worker / future API
```

`IRevalidationPolicy` decides whether to retry. `IRevalidationSchedulePolicy` decides when. The lifecycle coordinator
persists a transition and pending outbox item. `IRevalidationScheduler` publishes a scheduled message. The processor
loads and validates one logical retry. The worker owns broker settlement. All reusable services are asynchronous and
cancellation-aware.

The console-facing `IEmailValidator` is a lifecycle decorator around the same `IEmailValidationService` used by the
worker. A future API can resolve `IEmailValidator`, or compose `IEmailValidationService` with
`IValidationLifecycleCoordinator`, without referencing console or Azure types.

## Lifecycle and delivery semantics

- The initial validation is attempt 1. Provider `MaxRetries = 1` means two total attempts.
- Only transient/inconclusive `Unknown` results are eligible. Syntax, routing, suppression, definitive rejection, and
  conclusive results are final.
- Finality is independent of classification: `Unknown + Provisional` has retry work remaining;
  `Unknown + Final` is exhausted or not retryable.
- `ValidationId` is stable. Each successful compare-and-set appends a compact attempt record and replaces the canonical
  current result. A partial unique index permits one provisional lifecycle per normalized address, and identical
  single-flight results reuse it. Older messages cannot overwrite a newer version.
- Messages contain the lifecycle ID and scheduling metadata, not the email address, raw evidence, credentials, or SMTP
  transcript. `MessageId` is `{ValidationId}:{AttemptNumber}` and `MessageVersion = 1`.
- Scheduled time is the maximum of `RetryAfter`, current local/provider cooldown, provider policy-block cooldown, and
  existing failure backoff. The broker adapter uses native scheduled enqueue; no process sleeps until due.
- A currently cooling provider/domain causes the same logical attempt to be rescheduled without SMTP work. It never
  cycles senders, IPs, or proxies.
- The embedded outbox is committed with provisional state. Scheduling success clears it and sets
  `RetryScheduled = true`; failure leaves it pending and accurately reports `RetryScheduled = false`.
- Broker duplicate detection is optional. Application idempotency treats already-final, duplicate, and stale
  deliveries as successful no-work outcomes.
- Unsupported/malformed messages and missing lifecycle state go to the built-in DLQ. Transient infrastructure failures
  are abandoned for redelivery. Validation outcomes such as a continuing Microsoft policy block are not dead-lettered.

## Configuration

Store values under `EmailValidation:*` in the existing Azure App Configuration environment. The connection string can
be a Key Vault reference. Never commit or log it.

```json
{
  "EmailValidation": {
    "Persistence": {
      "Enabled": true,
      "Provider": "MongoDB",
      "DatabaseName": "email-validation",
      "LifecycleCollection": "EmailValidationLifecycle"
    },
    "Revalidation": {
      "Enabled": true,
      "DefaultMaxAttempts": 2,
      "OutboxDispatchIntervalSeconds": 30,
      "OutboxBatchSize": 100,
      "OutboxLeaseSeconds": 60,
      "ServiceBus": {
        "ConnectionString": "<App Configuration value or Key Vault reference>",
        "QueueName": "email-validation-retry",
        "ProvisionQueue": false,
        "EnableDuplicateDetection": false,
        "MaxDeliveryCount": 10,
        "MaxConcurrentCalls": 4,
        "PrefetchCount": 0,
        "MaxAutoLockRenewalMinutes": 10
      }
    }
  }
}
```

Provider cooldown and retry counts stay in the existing `Scheduling:ProviderPolicies` section; there is no duplicate
provider policy configuration under revalidation. Startup fails clearly if revalidation is enabled without Mongo, a
queue name, or a Service Bus connection. Disabled revalidation resolves no lifecycle store or broker client.

Duplicate detection defaults to off because a still-cooling provider reschedules the same deterministic attempt ID.
Enable it only when its history window is shorter than every possible same-attempt reschedule; Mongo lifecycle
idempotency remains required either way.

For local secret-reference resolution without Azure CLI, configure the local secret and its exact allowed URI:

```text
Azure__ServiceBusConnectionSecretUri=https://example.vault.azure.net/secrets/email-validation-service-bus
EmailValidation__Revalidation__ServiceBus__ConnectionString=<local secret>
```

Production can use an App Configuration connection string with direct Service Bus configuration and no local Azure
identity. If Key Vault references are used, use the host's managed/workload identity instead of a developer login.

## Provision and run

Provision with a deployment identity holding Service Bus Manage permission by temporarily setting
`ProvisionQueue=true` and starting either host. The initializer is idempotent. Set it back to `false` for normal runtime
so console/API producers can be limited to Send. The combined worker needs Listen for consumption and Send for its
outbox publisher; those claims can be split if publication is deployed as a separate host later. Separate host-specific
secrets may use the same key in separate deployment environments.

```bash
dotnet run --project src/EmailValidation.Worker
```

Console invocation is unchanged. Retryable results exit normally with additive fields such as `validationId`,
`resultState`, `attemptNumber`, `maximumAttempts`, `retryScheduled`, and `retryAfter`.

## Observability and verification

The `EmailValidation.Revalidation` meter records scheduled, received, executed, fresh-result skip, already-final,
rescheduled, finalized, exhausted, duplicate, stale, DLQ, and worker-failure events. Events use provider and transition
labels but never email addresses. Histograms cover processing latency, time to final, and attempts to final. The
in-process snapshot exposes Microsoft provisional/scheduled/executed/resolved/final-unknown counts.

Offline tests use fake policies, clocks, stores, schedulers, validators, and throttles. Real Mongo coverage is opt-in:

```bash
EMAIL_VALIDATION_TEST_MONGO='<connection string>' dotnet test --filter Category=MongoIntegration
```

The Mongo test creates a uniquely named collection, verifies idempotent indexes, outbox claiming, and compare-and-set
protection, then drops only that test collection. Broker provisioning and live delivery should be verified in the
target Azure environment with a deployment-owned test queue; normal unit tests require no Azure, Mongo, SMTP, DNS, or
wall-clock waits.
