# Real-time validation status gap analysis

## Classification before implementation

| Requirement | Classification | Existing implementation / action |
|---|---|---|
| Stable `ValidationId` | Implemented but incomplete | Present on results, Mongo lifecycle, retry messages, and attempt history, but allocated after validation. Extended the lifecycle boundary so it is allocated and persisted before validation starts. |
| Result finality, attempt number, retry time, retry scheduled | Already implemented correctly | `ValidationResultState`, `AttemptNumber`, `MaximumAttempts`, `NextRetryAt`, and `RetryScheduled` already existed. Reused them in status snapshots. |
| Retry calculation and cooldown | Already implemented correctly | `IRevalidationSchedulePolicy` already combines backoff, result `RetryAfter`, provider policy, local cooldown, and attempt number. Status reports its absolute result and does not recalculate it. |
| Lifecycle and attempt persistence | Already implemented correctly | `IValidationLifecycleStore` and `MongoValidationLifecycleStore` already provide compare-and-set lifecycle persistence and compact attempt history. Extended the same document rather than adding another store. |
| Durable retry/outbox | Already implemented correctly | The Mongo-backed outbox and Azure Service Bus worker remain the only retry work queue. |
| Validation/revalidation/single-flight services | Already implemented correctly | `IEmailValidationService`, `IValidationLifecycleCoordinator`, `IEmailRevalidationProcessor`, and `IValidationSingleFlight` already exist and remain transport-neutral. |
| Lifecycle phase separate from mailbox result | Missing | Added `ValidationLifecycleState`; mailbox `EmailValidationStatus` and `ValidationResultState` remain independent. |
| Monotonic status sequence | Missing | Added a lifecycle `Sequence` incremented only for meaningful persisted transitions. Mongo's internal CAS/outbox `Version` remains separate. |
| Current status query | Missing | Added `IValidationStatusQueryService`, backed by the canonical lifecycle store. |
| Status publication/subscription | Missing | Added host-neutral contracts, a Mongo change-stream subscription for distributed hosts, and an in-memory fallback, both with late-subscriber snapshot and sequence deduplication. |
| gRPC status API | Missing | Added versioned protobuf query and server-streaming watch RPCs with mapping, cancellation, access-policy, and host rate-limit boundaries. |
| SignalR, `IObservable`, or existing event bus | Missing / not applicable | None existed. No parallel SignalR or WebSocket path was added. |

## Persistence and delivery rules

Every published transition follows `persist -> publish`. Publisher failures are logged and do not change mailbox classification or roll back lifecycle persistence. A subscriber registers first, reads the current canonical snapshot, suppresses stale/duplicate sequences, and then receives live changes. A final snapshot/event completes the stream.

The validation engine reports only coarse, application-meaningful progress (`DomainChecks`, `ProviderChecks`, `SmtpValidation`, or `PersistedIntelligence`) through a host-neutral progress contract. The reporter persists the stage and then publishes it; it does not inspect or poll SMTP internals and it never exposes raw SMTP responses.

Mongo deployments use a filtered change stream over the authoritative lifecycle collection, so updates persisted by API, console, or worker processes reach every gRPC host without reusing the retry queue. Mongo must run as a replica set or sharded cluster with change-stream support. Non-Mongo/local deployments use the in-memory dispatcher and are intentionally limited to subscribers connected to the publishing process. The application contracts can still be replaced by a dedicated Service Bus topic, Redis pub/sub, or another backplane without changing lifecycle or gRPC code.

Historical event replay is not implemented. `after_sequence` suppresses stale delivery and reconnect returns the current state before continuing live delivery.

The default access policy is explicitly replaceable and currently unrestricted because the repository has no tenant ownership model. Production commercialization must replace it with a tenant-aware policy and configure the host's authentication handler; neither concern is embedded in Domain or Application code.

## Operational handoff notes

- Start the status host with `dotnet run --project src/EmailValidation.Grpc`. Its default Kestrel protocol is HTTP/2;
  deployment ingress must preserve HTTP/2 for server streaming.
- `GetValidationStatus` always reads the canonical lifecycle store. `WatchValidationStatus` opens the live subscription,
  emits the current snapshot, then streams newer sequences until `Final` or `Failed`.
- A reconnecting client should send its last accepted sequence as `after_sequence`. There is no historical replay; the
  current snapshot resynchronizes state and later messages continue from there.
- Mongo-backed streaming requires a replica set or sharded cluster with change streams enabled. Confirm that the
  runtime identity can read the lifecycle collection and open a change stream before production rollout.
- The host currently allows 20 concurrent status RPCs per authenticated name or remote IP, with no queue. Tune this at
  the host boundary based on commercial plans and expected connection duration.
- Replace `UnrestrictedValidationAccessPolicy` and configure the hosting authentication scheme before exposing the
  endpoint externally. The replacement policy should validate the caller's subject/tenant against lifecycle ownership.
- Do not point status consumers at `email-validation-retry`; it remains a work queue, not an event stream.
- Monitor `EmailValidation.Status`, `EmailValidation.Status.Mongo`, `EmailValidation.Grpc`, and
  `EmailValidation.Revalidation` meters. Metric labels intentionally exclude email addresses and validation IDs.
