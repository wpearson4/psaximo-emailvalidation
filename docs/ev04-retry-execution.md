# EV04: fenced retry execution and durable dispatch generations

EV04 extends the existing Mongo lifecycle, outbox, and Service Bus retry flow. It adds no deployed service, collection, or package dependency. Deployment has not been performed.

## Execution ownership

Each retry must atomically acquire an execution lease before checking probe availability or validating. Acquisition records a random owner, a monotonically increasing fencing token, and an expiry. A concurrent delivery cannot acquire a lifecycle already executing. The worker renews its lease during work; renewal failure or expiry cancels its validation wait. An expired execution is recovered through the durable outbox rather than being restarted directly by redelivery.

Result commits and deferrals require the current owner, fencing token, lifecycle version, and an unexpired lease. Mongo checks expiry against server time as well as the injected clock. Acquisition and renewal cannot write an expiry already past on the server. Regular lifecycle writes cannot overwrite a leased execution. Renewal updates the lease expiry without changing the lifecycle version, so it does not race the worker's own result CAS.

The guarantee is one executing owner while its lease is valid and no stale-owner lifecycle commit. Cancellation cannot undo an SMTP command already transmitted. Hosts still require synchronized clocks; a significantly late host clock fails lease acquisition safely. Domain/mailbox evidence ordering across independent validations remains EV07 work.

## Dispatch identity

Every intentional new schedule, cooldown deferral, no-new-evidence deferral, or recovery increments the persisted `DispatchGeneration`. The observation attempt number changes only for an observation; deferrals and abandoned work retain the preceding completed attempt count.

New messages use message version 2 and an ID shaped as `evr2:<SHA-256 of validation ID>:<attempt>:<generation>`. IDs remain within Service Bus's 128-character bound even with maximum-length validation IDs. Repeated publication of the same pending generation keeps the same ID, including after a publish-before-acknowledgement crash. A later generation gets a different ID even when the attempt and scheduled time are unchanged. Old publication acknowledgements cannot clear a newer pending generation.

Version-1 messages retain their original IDs and are accepted only for generation-zero lifecycle state. The existing CLR message record name is retained for source compatibility; its `MessageVersion` and `DispatchGeneration` fields define the wire contract. Malformed/unsupported messages produce fixed diagnostics without echoing recipient data. Ordinary Unknown/Invalid results and durable cooldown deferrals complete normally rather than being dead-lettered. If publication fails after a deferral is persisted, the durable outbox owns publication recovery.

## Recovery and migration

Recovery queries use indexed lifecycle state and UTC BSON date fields, rather than scanning unrelated provisional records. Overdue waiting dispatches receive a new generation. Expired running attempts lose their lease and are requeued at the same observation number. Recovery preserves the fencing counter and mailbox identity; acquisition of the recovered dispatch advances the fence again. Legacy running records with an old pending outbox entry are also recovered; their old entry cannot be republished or acknowledged into a waiting state while execution is marked running.

Startup adds the recovery indexes and backfills `RecoverySchemaVersion`, lifecycle state, and retry due time from legacy payloads using version-checked updates. It does not rewrite mailbox evidence. Repeated initialization is safe. Malformed legacy payloads are logged with a fixed diagnostic and excluded from automatic reconstruction.

Repeated abandoned executions are bounded independently of the observation budget. After the configured recovery allowance, the canonical lifecycle becomes Failed/Final with an execution-failure explanation and no further retry. The recovery publisher projects recovered lifecycles into job results. The previously documented projection-outbox failure is separate from this retry outbox implementation.

All settings are under `EmailValidation:Revalidation`:

| Setting | Default | Behavior |
| --- | ---: | --- |
| `ExecutionLeaseSeconds` | 120 | Execution ownership lifetime |
| `ExecutionRenewalSeconds` | 30 | Renewal interval; must be positive and at most one third of the lease |
| `MaximumExecutionRecoveries` | 3 | Abandoned executions that may be requeued before terminal failure |
| `RetryRecoveryGraceMinutes` | 15 | Waiting-dispatch and legacy unleased-execution recovery grace |

An expired modern execution lease does not wait for the waiting-dispatch grace. Recovery no longer waits for the broker duplicate-detection window because it creates a new generation.

## Verification and deployment gate

Verified locally on 2026-10-09: **648 Core, 52 API, 2 gRPC, and 9 Mongo tests passed** (711 total). The full solution build completed with zero warnings and errors, and `git diff --check` passed. All Mongo tests used a temporary localhost database. The cloud broker test and the known baseline projection-outbox failure are excluded from this passing count.

Synthetic tests exercise two independent processors, renewal during long work, renewal cancellation, expired-owner rejection, stale result rejection, repeated deferrals at the same time, bounded recovery, and versioned/legacy serialization. Mongo tests exercise two separate child processes contending for a lease, independent store instances, renewal/recovery races, server-time expiry, stale fencing tokens even with a current CAS version, publish/acknowledgement crash windows, and legacy metadata migration without starvation from unrelated states. The child executable under `tests/EmailValidation.RevalidationProbe` is test-only.

Run local tests with external probes and cloud provisioning disabled, supplying only an isolated Mongo test instance:

```sh
EMAIL_VALIDATION_TEST_SERVICEBUS_ALLOW_PROVISION=0 \
EMAIL_VALIDATION_RUN_LIVE_TESTS=0 \
EMAIL_VALIDATION_TEST_MONGO=mongodb://127.0.0.1:27916 \
EMAIL_VALIDATION_TEST_MONGO_DATABASE=ev04_integration \
dotnet test EmailValidation.sln --no-restore -m:1 -p:UseSharedCompilation=false \
  --filter 'Category!=Live&Category!=ServiceBusIntegration&FullyQualifiedName!~Outbox_IsIdempotentAtomicallyClaimedReclaimableAndTtlSafe'
```

The excluded projection-outbox test has a pre-existing Mongo parallel-array-index failure, independently reproduced before EV04. It is not claimed as passing.

`ServiceBusRevalidationDispatchTests` is a separate opt-in gate for real broker duplicate detection, same-generation republishing after publisher restart, intentional new generations, lock expiry, and receiver restart. It requires an approved isolated namespace in `EMAIL_VALIDATION_TEST_SERVICEBUS` and `EMAIL_VALIDATION_TEST_SERVICEBUS_ALLOW_PROVISION=1`. It creates and deletes one temporary `ev04-test-*` queue. This gate has not run: automatic approval review rejected conditional queue provisioning against an unspecified namespace. Obtain approval for the concrete test namespace before enabling it. No cloud queue was created or changed.

Before deployment, stop old retry workers and lifecycle writers, initialize the additive metadata/index migration, then start the updated fleet together. Do not mix old workers with version-2 producers: old workers lack fencing and reject the new message version. Preserve queued version-1 messages for compatible generation-zero processing or recovery. Run the approved broker gate before enabling production retry traffic. Rolling back requires quiescing the fleet and reconciling version-2 dispatches; an in-place rollback to unfenced writers is unsafe.
