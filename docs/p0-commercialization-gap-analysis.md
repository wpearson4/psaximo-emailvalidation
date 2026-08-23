# P0 commercialization gap analysis

Assessment date: 2026-08-22. Repository evidence, rather than the starting assumptions, determined each classification.

| Capability | Discovery status | Evidence | Confirmed gap | Required/completed work |
|---|---|---|---|---|
| Persistent domain/mailbox intelligence | IMPLEMENTED | `MongoValidationIntelligenceStore`, `DomainIntelligenceService`, `IntelligenceEmailValidator`, Mongo integration tests | None. Reads occur before planning/live execution; Mongo read/write failures are treated as loss of reuse rather than classification evidence. | No implementation change. |
| Result reuse | IMPLEMENTED | `IntelligenceEmailValidator`, `ValidationResultReusePolicy`, `InMemoryValidationResultCache`, `ValidationReuseAndSingleFlightTests` | None. Memory and persistent reuse preserve `ValidatedAt`; stale mailbox and fresh domain planning are independent. | No implementation change. |
| Single-flight deduplication | IMPLEMENTED | `ValidationSingleFlight`, mailbox execution keys, `DomainSingleFlight` in `DomainIntelligenceService`, concurrency/cancellation/failure tests | None for process-local P0 behavior. | No implementation change. |
| Durable automatic retry | IMPLEMENTED | `ValidationLifecycleCoordinator`, Mongo lifecycle/outbox, `AzureServiceBusRevalidationScheduler`, worker settlement logic, deterministic `MessageId`, retry tests | No retry transport gap. Lifecycle reporting lacked the distinct `RetryScheduled` transition (tracked below). | No parallel retry implementation added. |
| REST API | NOT IMPLEMENTED | No API project or HTTP endpoints existed. | Commercial validation and status query adapters were absent. | Added `EmailValidation.Api` with thin validation, status, job, and result endpoints using existing Application/Core services. |
| Asynchronous validation jobs | NOT IMPLEMENTED | Retry messages existed, but no customer batch job model/store/queue/processor existed. | Separate job lifecycle, durable data, bounded processing, progress, and result retrieval were absent. | Added Application job contracts/services, Mongo job/item persistence, Service Bus identifier messages, and a bounded worker. |
| IDN / SMTPUTF8 correctness | PARTIALLY IMPLEMENTED | `EmailNormalizer` already used `IdnMapping` and IDN tests existed. SMTP used ASCII and did not model Unicode local parts or parse SMTPUTF8. | Unicode-local transport requirements and destination capability evidence were missing. | Extended the existing normalizer and SMTP probe with `RequiresSmtpUtf8`, EHLO capability parsing, UTF-8 commands only when supported, explicit unsupported evidence, result metadata, metrics, and tests. |
| Bulk CSV validation | IMPLEMENTED | `CsvFileProcessor`, `CsvInput`, `DomainValidationScheduler`, `CsvFileProcessorTests` | None. Existing implementation detects/configures the column, preserves data/order/timestamps, uses bounded processing and the canonical validator, and atomically replaces output. | No implementation change. |
| Canonical lifecycle/status | PARTIALLY IMPLEMENTED | Mongo lifecycle store, status query/publisher/subscription abstractions, gRPC v1 query/watch adapter, lifecycle tests | Persisted states had `Provisional` and `RetryWaiting` but no explicit `RetryScheduled` transition. | Added an append-only enum value and a persisted/published scheduling transition before Service Bus submission. Existing numeric values remain stable. |

## Architecture and compatibility decisions

- Mongo remains the source of truth for reusable intelligence, validation lifecycle, and durable job data.
- Mongo operations use ordinary indexes, filters, inserts, and updates supported by MongoDB 4.4; no MongoDB 5+ aggregation/update features were introduced.
- REST, CSV, jobs, and retry workers all terminate at the existing canonical validator/application pipeline.
- Service Bus job messages contain only `JobId`; email inputs and results stay in Mongo and are processed in bounded chunks.
- The existing gRPC host was retained. Moving it solely to match a preferred project layout would replace working behavior without an architectural need.
- No Redis, PostgreSQL, Kafka, RabbitMQ, validation-depth tier, outbound IP rotation, or sender rotation behavior was added.

## Deferred by scope

- Tenant authentication, quotas, rate limits, billing, and usage metering remain future commercial concerns. `IValidationAccessPolicy` remains the authorization seam for status access.
- Distributed single-flight across multiple host processes remains deferred; durable lifecycle/outbox idempotency and Service Bus duplicate detection protect durable continuations, while current validation single-flight is process-local.
- Live Azure Service Bus and Mongo job integration tests require deployment-owned infrastructure and credentials. Offline unit/API coverage verifies serialization-independent orchestration, bounded execution, progress, partial errors, and endpoint behavior.
