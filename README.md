# Email Validation Service

A .NET 10 email-validation platform whose validation engine is isolated from its Console, Worker, REST, and gRPC hosts. It gathers structured evidence and returns `Valid`, `LikelyValid`, `CatchAll`, `Risky`, `LikelyInvalid`, `Invalid`, or `Unknown`; it does not claim that SMTP can always prove mailbox existence.

## Production API host

`EmailValidation.Api` is the secure, versioned commercial REST and gRPC integration host. It reuses the same validation core, canonical lifecycle, Mongo persistence, Service Bus retry/job workers, and status subscriptions used elsewhere in the solution.

See [API operations and deployment](docs/api-deployment.md) for OAuth scopes, REST/gRPC usage, OpenAPI/Swagger, Docker, health checks, and the 10.10.252.31 deployment. The discovery record is in [production API gap analysis](docs/production-api-gap-analysis.md).

## Projects

- `src/EmailValidation.Domain` — immutable business models, enums, value objects, and risk/intelligence semantics. The existing `EmailValidation.Core` namespace is retained for binary/source compatibility while ownership moves to the Domain assembly.
- `src/EmailValidation.Application` — host-independent domain-intelligence orchestration, freshness/compatibility policy, bounded concurrency, and domain-level single-flight.
- `src/EmailValidation.Core` — compatibility-facing contracts plus the existing normalization, mailbox orchestration, classification, and confidence policies; new domain and application functionality is kept out of hosts.
- `src/EmailValidation.Infrastructure` — MX/DNS, SMTP, catch-all probing, throttling, caching, provider detection, and domain intelligence.
- `src/EmailValidation.Console` — command parsing, configuration, batch ingestion, output, diagnostics, and logging bootstrap.
- `src/EmailValidation.Worker` — Azure Service Bus receive adapter and durable revalidation outbox publisher.
- `src/EmailValidation.Api` — production composition host for versioned REST/gRPC, OAuth scopes, OpenAPI, health, and deployment controls.
- `src/EmailValidation.Grpc` — reusable versioned unary validation/status and server-streaming transport contracts hosted by the API.
- `tests/EmailValidation.Core.Tests` — offline unit tests using fakes.
- `tests/EmailValidation.Grpc.Tests` — protobuf contract mapping tests.
- `tests/EmailValidation.IntegrationTests` — opt-in live network tests.

User-visible changes and migration notes are recorded in [CHANGELOG.md](CHANGELOG.md).

The console is only a host. A future API or worker can call `IEmailValidator` after registering `AddEmailValidation()`.

Durable automatic revalidation is documented in [docs/automatic-revalidation.md](docs/automatic-revalidation.md),
including its architecture gap analysis, configuration, lifecycle semantics, and operations.
Real-time lifecycle status, gRPC reconnect semantics, authorization boundaries, and distributed delivery are
documented in [docs/realtime-status-gap-analysis.md](docs/realtime-status-gap-analysis.md).
The current boundary assessment, SOLID review, remediated risks, and dependency guardrails are documented in
[docs/architecture-review-guardrails.md](docs/architecture-review-guardrails.md).

## Run

The console loads `EmailValidation:*` settings from Azure App Configuration and resolves Key Vault references with `DefaultAzureCredential`. Local development uses the current Azure CLI identity:

```bash
az login
az account set --subscription "Visual Studio Professional"
```

The configured bootstrap endpoint is `https://appcs-p-ometa-dsi-scus.azconfig.io`; `AZURE_APPCONFIG_ENDPOINT` can override it for another deployment. When Azure identity is unavailable, local `Azure:AppConfigurationConnectionString` and `EmailValidation:Persistence:ConnectionString` values can bootstrap App Configuration and resolve its Mongo Key Vault reference without `az login`; `Azure:MongoConnectionSecretUri` restricts that local override to the intended secret. Treat both connection strings as secrets and never commit them. The configured label is `Production`, matching the existing OpenMeta environment convention. Use a different label when a development App Configuration/Mongo environment is provisioned—do not point an ad hoc development run at production.

Production hosts do not depend on a developer Azure session: use an App Configuration connection string or workload/managed identity for bootstrap, and store the Service Bus connection string as an `EmailValidation:*` App Configuration value or Key Vault reference. Local Service Bus secret overrides use `Azure:ServiceBusConnectionSecretUri` with `EmailValidation:Revalidation:ServiceBus:ConnectionString` and are never logged.

From this directory:

```bash
dotnet run --project src/EmailValidation.Console -- validate john@example.com
dotnet run --project src/EmailValidation.Console -- validate john@example.com jane@example.org --format json
dotnet run --project src/EmailValidation.Console -- file emails.csv
dotnet run --project src/EmailValidation.Console -- file emails.csv --column BUSINESS_EMAIL
dotnet run --project src/EmailValidation.Console -- interactive
dotnet run --project src/EmailValidation.Console -- diagnostics smtp
dotnet run --project src/EmailValidation.Grpc
dotnet run --project src/EmailValidation.Api
```

## REST and asynchronous jobs

The API exposes `POST /v1/email/validate`, `GET /v1/email-validations/{validationId}`,
`POST /v1/email-validation/jobs`, `GET /v1/email-validation/jobs/{jobId}`, and
`GET /v1/email-validation/jobs/{jobId}/results`. Endpoints are transport adapters over the same canonical
validator used by CSV and workers. A provisional validation returns immediately; its durable retry continues
independently.

When `EmailValidation:Jobs:Enabled` is true, Mongo stores job headers and ordered item results in
`EmailValidationJobs` and `EmailValidationJobItems`. Service Bus queue `email-validation-jobs` receives only a
job identifier. `ChunkSize` and `MaximumConcurrency` bound execution; original positions are retained for ordered
result retrieval. The connection string is resolved through the existing App Configuration/Key Vault path.

Unicode domains are normalized with the platform IDNA implementation. Unicode local parts remain valid and are
marked `RequiresSmtpUtf8`. SMTP probes parse EHLO capabilities and do not send an internationalized recipient when
the destination does not advertise `SMTPUTF8`; the result records explicit inconclusive evidence instead of
classifying the mailbox or domain as invalid.

DNS/MX lookup is part of normal validation. SMTP mailbox and catch-all probing require the explicit `--live` switch:

```bash
dotnet run --project src/EmailValidation.Console -- validate test@example.com --live --verbose
```

Before live use, configure Elasticsearch as the source of authorized sender identities. Individual addresses are not stored in application settings. The Query DSL object is deployment-owned and is sent as the request's `query`; only the configured email field is returned:

```json
{
  "EmailValidation": {
    "ProbeSenderSource": {
      "Provider": "Elasticsearch",
      "Endpoint": "https://elasticsearch.example-owned-domain.com:9200",
      "Index": "authorized-probe-senders",
      "EmailField": "business_email",
      "QueryLimit": 500,
      "RefreshThreshold": 100,
      "Query": {
        "bool": {
          "filter": [
            { "exists": { "field": "business_email" } }
          ]
        }
      }
    },
    "ProbeSenderRotation": {
      "MaxValidationsPerSender": 50,
      "MaxActiveMinutes": 15,
      "MaxSenderAttemptsPerValidation": 2,
      "SenderAffinityMinutes": 60,
      "RotateOnSenderSpecificFailure": true
    },
    "Scheduling": {
      "GlobalConcurrency": 8,
      "PerDomainConcurrency": 1,
      "DomainMinIntervalMilliseconds": 1500,
      "DomainIntervalJitterMilliseconds": 250,
      "MaxActiveDomains": 1000,
      "DefaultProviderPolicy": {
        "PerProviderConcurrency": 2,
        "DelayMilliseconds": 1500,
        "PolicyBlockCooldownMinutes": 15,
        "MaxRetries": 1
      },
      "ProviderPolicies": {
        "Yahoo": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 4000,
          "PolicyBlockCooldownMinutes": 60,
          "MaxRetries": 1
        },
        "MicrosoftConsumer": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 4000,
          "PolicyBlockCooldownMinutes": 90,
          "MaxRetries": 1
        },
        "Microsoft365": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 3000,
          "PolicyBlockCooldownMinutes": 60,
          "MaxRetries": 1
        },
        "Google": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 3000,
          "PolicyBlockCooldownMinutes": 45,
          "MaxRetries": 1
        },
        "AppleICloud": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 4000,
          "PolicyBlockCooldownMinutes": 60,
          "MaxRetries": 1
        },
        "Comcast": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 3000,
          "PolicyBlockCooldownMinutes": 45,
          "MaxRetries": 1
        },
        "Proton": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 4000,
          "PolicyBlockCooldownMinutes": 60,
          "MaxRetries": 1
        },
        "Zoho": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 2500,
          "PolicyBlockCooldownMinutes": 45,
          "MaxRetries": 1
        },
        "Fastmail": {
          "PerProviderConcurrency": 1,
          "DelayMilliseconds": 2500,
          "PolicyBlockCooldownMinutes": 45,
          "MaxRetries": 1
        }
      }
    }
  }
}
```

Store `Username`/`Password` or `ApiKey` through user secrets or environment variables (for example, `EmailValidation__ProbeSenderSource__ApiKey`), never in committed configuration. Anonymous Elasticsearch is also supported when the deployment allows it.

The live probe opens port 25, issues `EHLO`, `MAIL FROM`, and `RCPT TO`, resets the envelope, then quits. It never sends `DATA` or message content. A bounded, process-wide pool is loaded once and shared by interactive and CSV validation. Healthy senders are held by domain-scoped affinity even when the routine pool rotation threshold is reached. Syntax is checked while loading; sender-domain DNS health is checked lazily before first use. A sender-specific rejection clears only that recipient domain's affinity and records domain/sender incompatibility; globally invalid sender health removes every matching affinity. Recipient rejection, rate limiting, anti-abuse, provider-wide, and source-IP failures never trigger identity rotation. Sender fallback and all other SMTP work share the strict per-address session budget.

## CSV input

A CSV file must have a header. Common email headers are detected automatically, case-insensitively, after normalizing spaces, underscores, and hyphens:

```csv
email
john@example.com
jane@example.org
```

If multiple plausible email columns exist, select one with `--column`; an explicit column always takes precedence. The command invokes the same `IEmailValidator` pipeline used by `validate`, preserves row order, and appends or updates `Status`, `Classification Confidence`, `Confidence Reason`, `Evidence Quality`, `Confidence Type`, `Deliverability Probability`, `Catch-All Classification`, `Probe Attempted`, `Probe Disposition`, `SMTP Response Category`, `Retry After`, `Validation Date/Time`, `Validation ID`, `Result State`, attempt limits, scheduling state, and lifecycle timestamps. New files no longer receive a duplicate `Confidence` column; a legacy column already present in an input file is preserved and refreshed for compatibility.

The source file is replaced only after all rows have been validated and a same-directory temporary CSV has been flushed and verified. Cancellation or a parse/write failure leaves the original unchanged. UTF-8 BOM behavior, quoted commas, quoted quotes, embedded newlines, empty values, and Unicode data are supported. Processing uses bounded, ordered batches. Each batch is grouped by the normalized recipient domain and scheduled round-robin, while completed rows are written in original sequence order. Existing domain locks and caches reuse DNS, MX, provider, catch-all, and behavioral intelligence. Duplicate normalized addresses share an active validation and then reuse its hot result; every input row still receives output in original order. For reused results, `Validation Date/Time` remains the underlying evidence time rather than the later return time.

## Classification and confidence

Classification is centralized in `EmailClassificationEngine`. Definitive syntax, NXDOMAIN, missing or definitively unroutable infrastructure, and explicit recipient rejection evidence can produce `Invalid`. Timeouts, temporary responses, connection failures, and blocked verification produce `Unknown`. A locally skipped probe is separately reported as `LocalCooldown`, with `Probe Attempted = No` and a `Retry After` value when available. Any accepted recipient with current or sufficiently repeated historical catch-all evidence is classified as `CatchAll`; unresolved gateway acceptance is also exposed as `CatchAll` with the `GatewayAmbiguous` subtype unless randomized-recipient rejection establishes that the domain is likely not catch-all. The address is technically deliverable, although SMTP cannot prove that the specific mailbox exists. Catch-all classification takes precedence over `Risky`, while disposable, role-account, suppression, and other concerns remain available in the detailed mailing-risk result. Mailbox-full evidence remains `Risky`, not a false mailbox-not-found result.

The top-level classification is accompanied by additive typed detailed statuses, evidence provenance, `BounceRisk`, and a separate send recommendation. The recommendation is campaign policy rather than a rewrite of technical validity: for example, an accepted but configured suppression match can remain technically deliverable while `recommendation.send` is `false`.

The legacy `confidence` value and additive `classificationConfidence` value are heuristic measures of confidence in the selected classification, not statistically calibrated delivery probabilities. Thus `Unknown` with 87% confidence means the engine is highly confident that verification was inconclusive; it does not mean the mailbox has an 87% delivery probability. `deliverabilityProbability` remains null until authorized outcome data supports a genuinely calibrated estimate. The centralized model weighs syntax (`+0.20`), domain existence (`+0.20`), MX routing (`+0.15`), provider confidence (up to `+0.10`), provider-interpreted SMTP acceptance (`+0.12` to `+0.25`), and negative catch-all evidence (up to `+0.10`). Risk and ambiguity evidence is handled according to the final status. Verbose mode lists every contribution and explanation.

Catch-all inference is deliberately conservative. One accepted randomized recipient is insufficient for ordinary providers; they require at least two accepted probes before `LikelyCatchAll`. Microsoft EOP random-recipient acceptance is immediately recorded as likely gateway-or-catch-all behavior so a target acceptance cannot be mistaken for mailbox proof. Google Workspace randomized-recipient acceptance remains internally uncertain because an SMTP `RCPT TO` acceptance does not establish final routing for an unrecognized recipient; when the target is accepted through that unresolved gateway, the public status is `CatchAll` with subtype `GatewayAmbiguous`. Verbose results expose accepted, rejected, and ambiguous probe counts plus the interpretation used.

Fresh domain-level `LikelyCatchAll` evidence at or above `CatchAll:MinimumReusableConfidence` is persisted through the configured domain-intelligence store. Later addresses on the same unchanged MX/provider topology reuse its structured reason and observation time, skip randomized-recipient discovery, and skip recipient SMTP when acceptance cannot distinguish mailbox existence. Reused results report `PersistentDomainIntelligence`; they remain `CatchAll` rather than being promoted to `Valid`. Expiry, MX changes, provider-strategy version changes, and inconclusive-refresh backoff are handled by the shared validation planner and domain lock.

Provider detection returns both a provider and confidence from centralized MX signatures. Microsoft consumer and Microsoft 365 infrastructure have separate pacing policies; Google Workspace, Yahoo, Apple/iCloud, Comcast, Proton, Zoho, Fastmail, Proofpoint, and Mimecast are also recognized. AOL, AT&T-hosted, and Verizon legacy MX routes normalize to the shared Yahoo infrastructure policy so they cannot probe the same provider concurrently under different labels. The existing provider strategies remain responsible for interpreting SMTP evidence. Provider policies add process-wide concurrency, minimum intervals, bounded retries, and policy-block cooldowns without replacing domain pacing. After a policy cooldown, one half-open probe decides whether the provider resumes or returns to cooldown. Google `421`/`451` responses with `4.7.x` enhanced codes are treated as rate-limited, inconclusive evidence rather than mailbox rejection. Gateway acceptance is recorded separately from strong mailbox evidence. SMTP results include a complete command-stage session, basic and enhanced status codes, normalized category, text classification, failed stage, MX, provider, attempt, timing, banner/EHLO/TLS diagnostics, and sanitized response excerpts. A recipient rejection is definitive only when session evidence proves that `MAIL FROM` succeeded and `RCPT TO` returned a recipient-specific permanent rejection.

MongoDB is the configured durable intelligence provider. It uses the existing `IValidationIntelligenceStore` and `IValidationObservationStore` abstractions, a shared `MongoClient`, and two service-owned collections: `EmailValidationDomainIntelligence` and `EmailValidationMailboxIntelligence`. Domain IDs are normalized domains; mailbox IDs are SHA-256 hashes of normalized addresses. Domain observations are bounded and embedded in the domain document so topology-specific history survives process restarts without creating another collection. Startup creates missing collections/indexes idempotently and never drops or recreates data.

The Mongo connection string is an Azure App Configuration Key Vault reference (`EmailValidation:Persistence:ConnectionString`, label `Production`) to the existing Key Vault secret. It is not present in source, local settings, command arguments, or logs. Persisted mailbox/domain payloads omit SMTP sessions, response text, diagnostic server text, and catch-all probe payloads. Existing delivery-outcome and suppression abstractions retain their current JSON implementation; they were not migrated because this change only adds the two reusable validation-intelligence collections. The contracts keep the engine replaceable by another durable host implementation.

Result execution follows one host-neutral path: normalize the address, check the bounded process-local result cache, evaluate persisted mailbox intelligence against fresh domain evidence, and only then enter single-flight for live work. Fresh conclusive mailbox results are reused only when all engine/classification/confidence/provider policy versions match, their strong evidence is inside the configured status-specific window, their SMTP strength satisfies the request, and MX topology has not changed. Recent transient/provider-block outcomes use a separate short TTL to prevent immediate repeat probes without treating them as mailbox evidence. A persistent hit warms memory; a successful live result is persisted and replaces the hot entry.

Concurrent requests for the same normalized address, request mode, diagnostics mode, and policy identity use a process-local single-flight operation, so one live validation serves all callers. One waiter cancelling does not cancel work still needed by other callers; failed flights are removed and can be retried. Results expose additive source metadata (`LiveValidation`, `MemoryCache`, `PersistentReuse`, or `JoinedInFlightValidation`) plus original validation time, return time, and reuse age. Distributed locking is deliberately outside the console phase.

Authorized downstream systems can record `DeliveryOutcomeRecord` values containing an immutable `ValidationPredictionSnapshot` and an actual `Delivered`, `HardBounce`, `SoftBounce`, `Suppressed`, or `Unknown` outcome. `IConfidenceCalibrationService` reports cohort counts, delivery/bounce rates, false-valid/false-invalid rates, precision, recall, Brier score, calibration error, and confidence bands. It explicitly reports aggregate-only results until a sufficient calibrated-probability sample exists. Hard-bounce outcomes also create a source-attributed persistent suppression entry.

`IValidationQualityMetrics` provides host-neutral validation and provider summaries, including status/unknown rates, verification blocks, catch-all, disposable, typo, suppression, reliability, and latency. `IValidationPersistenceMetrics` separately records requests, persistence reads/hits/misses and latency, write success/failure, cache hits/writes/invalidations, persistent reuse, rejection causes, mailbox/domain reuse, live executions, single-flight leaders/joiners, avoided live work, and collapse ratio. No dashboard framework or console dependency is embedded in the engine.

Address intelligence is intentionally separate from domain intelligence. Conservative typo suggestions and free-provider detection work locally. Toxic-domain, known spam-trap, abuse-risk, suppression, and MX-forward results require explicitly configured application-owned intelligence. Alias, alternate-address, and domain-age contracts are available, but the default providers return `Unknown` because SMTP and DNS cannot establish those facts reliably.

Domain intelligence is now a first-class application service. It collapses concurrent work by normalized domain, reads memory before durable intelligence, refreshes only stale snapshots, bounds concurrent live analysis, and persists one immutable snapshot containing mail routing, DNSSEC, SPF/DMARC metadata, honest DKIM observation state, provider evidence, catch-all state, disposable-domain provenance, and topology lifecycle fingerprints. Catch-all probing has its own domain flight so a cancelled waiter cannot cancel work still needed by other callers. DNSSEC/authentication/dataset failures degrade their own intelligence sections without changing mailbox classification.

## Microsoft 365 validation boundary

Published MX hosts beneath `mail.protection.outlook.com` and `mx.microsoft` are recognized as Microsoft 365 family routes with `MicrosoftExchangeOnlineProtection` as the gateway. Gateway and mailbox providers are modeled separately: the Microsoft-owned MX identifies EOP, while the mailbox provider remains `Unknown` unless recipient-differentiating evidence supports Microsoft 365. A target accepted while randomized recipients are rejected is high-reliability evidence; acceptance of both is `GatewayAccepted`, leaves the mailbox unknown, and lowers reliability.

Exchange Online Protection is treated as the intended public SMTP validation boundary whenever it is the domain's preferred published MX. The validator does not discover alternate direct hosts, connect to undocumented backends, skip third-party gateways, cycle endpoints, or otherwise attempt to evade provider verification controls. Policy rejection, throttling, temporary failure, and ambiguous permanent failure remain `Unknown`; only recipient-specific rejection evidence can make a mailbox invalid.

Domain observations include target/random acceptance rates, recipient rejection, temporary failure, rate limiting, gateway acceptance, and a normalized MX-topology fingerprint. Recent cached catch-all behavior avoids repeated random probes. When the published topology changes, prior observations remain stored as history but are excluded from active decision-making until the new topology is observed.

## Configuration

Defaults are in `src/EmailValidation.Console/appsettings.json` and can be overridden with environment variables. Available controls include:

- DNS timeout/cache lifetime;
- SMTP enablement, sender cooldown/fallback limit, total session budget, connection/command timeout, retry count, and bounded greylisting retry delay;
- Elasticsearch endpoint/authentication, index, email field, configurable Query DSL, bounded query/refresh limits, and stale refresh interval;
- sticky sender rotation validation/time thresholds, bounded jitter, and MAIL FROM health threshold;
- global/per-domain/per-provider concurrency, bounded active domains, domain/provider pacing, bounded jitter, provider overrides, and exponential temporary-failure cooldown;
- sender-affinity and domain/sender compatibility lifetimes;
- catch-all enablement, probe count (clamped to 1–3), minimum accepted probes, and cache lifetime;
- configurable role names, disposable/free domains, and safe typo mappings;
- application-owned toxic-domain, known-trap, abuse-risk, suppression, and MX-forward intelligence;
- persistence provider/database/collection names; positive, negative, risky, and transient result-freshness windows; hot-cache enablement and size; single-flight enablement; and explicit engine/classification/confidence/provider policy versions;
- standard structured logging levels.

## Tests

```bash
dotnet test EmailValidation.sln
```

Normal tests require no public network. To enable the separate live DNS test:

```bash
EMAIL_VALIDATION_RUN_LIVE_TESTS=1 dotnet test tests/EmailValidation.IntegrationTests
```

Mongo integration coverage is opt-in and uses unique temporary collection names that are removed after the test:

```bash
EMAIL_VALIDATION_TEST_MONGO='mongodb://test-host/test-database' \
EMAIL_VALIDATION_TEST_MONGO_DATABASE='email-validation-integration-tests' \
dotnet test tests/EmailValidation.IntegrationTests --filter Category=MongoIntegration
```

## Prototype limitations

- Many providers deliberately obscure recipient existence, accept all recipients, greylist, or block port 25. Those outcomes are `Unknown` or `CatchAll`, not false `Invalid` claims. Remote blocks and locally deferred probes are reported separately.
- Domains without an explicit MX can use RFC implicit A/AAAA fallback. Results expose this as `usedImplicitMxFallback` and `ImplicitMxFallback`; configuring an explicit MX is strongly preferable operationally.
- Catch-all acceptance is modeled as likely evidence, never proof.
- Absence from a local disposable, toxic, trap, or abuse list is not represented as authoritative proof that an address is clean.
- Domain age, aliases, and alternate addresses remain `Unknown` until a reliable provider is registered.
- Provider strategies interpret acceptance conservatively: Google Workspace, Microsoft 365, Proofpoint, and Mimecast acceptance can represent gateway-level rather than mailbox-level acceptance.
- The hot cache, single-flight operation, and throttles are process-local abstractions intended to be replaced or coordinated for a distributed deployment; durable intelligence remains host-neutral behind interfaces.
- This phase intentionally contains no hosted API, queues, proxy/IP rotation, tenant/authentication, machine-learning calibration, or commercial validation-provider integration.
