# Email Validation Service prototype

A standalone .NET 10 console prototype whose validation engine is isolated from its host. It gathers structured evidence and returns `Valid`, `LikelyValid`, `Risky`, `LikelyInvalid`, `Invalid`, or `Unknown`; it does not claim that SMTP can always prove mailbox existence.

## Projects

- `src/EmailValidation.Core` — models, contracts, normalization, orchestration, classification, and confidence scoring.
- `src/EmailValidation.Infrastructure` — MX/DNS, SMTP, catch-all probing, throttling, caching, provider detection, and domain intelligence.
- `src/EmailValidation.Console` — command parsing, configuration, batch ingestion, output, diagnostics, and logging bootstrap.
- `tests/EmailValidation.Core.Tests` — offline unit tests using fakes.
- `tests/EmailValidation.IntegrationTests` — opt-in live network tests.

The console is only a host. A future API or worker can call `IEmailValidator` after registering `AddEmailValidation()`.

## Run

From this directory:

```bash
dotnet run --project src/EmailValidation.Console -- validate john@example.com
dotnet run --project src/EmailValidation.Console -- validate john@example.com jane@example.org --format json
dotnet run --project src/EmailValidation.Console -- file emails.csv
dotnet run --project src/EmailValidation.Console -- file emails.csv --column BUSINESS_EMAIL
dotnet run --project src/EmailValidation.Console -- interactive
dotnet run --project src/EmailValidation.Console -- diagnostics smtp
```

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

If multiple plausible email columns exist, select one with `--column`; an explicit column always takes precedence. The command invokes the same `IEmailValidator` pipeline used by `validate`, preserves row order, and appends or updates `Status`, `Confidence`, `Confidence Reason`, and `Validation Date/Time`.

The source file is replaced only after all rows have been validated and a same-directory temporary CSV has been flushed and verified. Cancellation or a parse/write failure leaves the original unchanged. UTF-8 BOM behavior, quoted commas, quoted quotes, embedded newlines, empty values, and Unicode data are supported. Processing uses bounded, ordered batches. Each batch is grouped by the normalized recipient domain and scheduled round-robin, while completed rows are written in original sequence order. Existing domain locks and caches reuse DNS, MX, provider, catch-all, and behavioral intelligence.

## Classification and confidence

Classification is centralized in `EmailClassificationEngine`. Definitive syntax, NXDOMAIN, missing or definitively unroutable infrastructure, and explicit recipient rejection evidence can produce `Invalid`. Timeouts, temporary responses, connection failures, and blocked verification produce `Unknown`. Disposable, role, likely catch-all, configured reputation, and high-confidence typo signals produce `Risky` unless a definitive invalid condition takes precedence. Mailbox-full evidence is preserved separately and produces `Risky`, not a false mailbox-not-found result.

The top-level classification is accompanied by additive typed detailed statuses, evidence provenance, `BounceRisk`, and a separate send recommendation. The recommendation is campaign policy rather than a rewrite of technical validity: for example, an accepted but configured suppression match can remain technically deliverable while `recommendation.send` is `false`.

The confidence value is a heuristic measure of confidence in the selected classification, not a statistically calibrated delivery probability. The centralized model weighs syntax (`+0.20`), domain existence (`+0.20`), MX routing (`+0.15`), provider confidence (up to `+0.10`), provider-interpreted SMTP acceptance (`+0.12` to `+0.25`), and negative catch-all evidence (up to `+0.10`). Risk and ambiguity evidence is handled according to the final status. Verbose mode lists every contribution and explanation. The model should later be calibrated against observed delivery outcomes.

Catch-all inference is deliberately conservative. One accepted randomized recipient is insufficient for ordinary providers; they require at least two accepted probes before `LikelyCatchAll`. Microsoft EOP random-recipient acceptance is immediately recorded as likely gateway-or-catch-all behavior so a target acceptance cannot be mistaken for mailbox proof. Google Workspace randomized-recipient acceptance remains `Unknown` because an SMTP `RCPT TO` acceptance does not establish final routing for an unrecognized recipient. Verbose results expose accepted, rejected, and ambiguous probe counts plus the interpretation used.

Provider detection returns both a provider and confidence from centralized MX signatures. Microsoft consumer and Microsoft 365 infrastructure have separate pacing policies; Google Workspace, Yahoo, Apple/iCloud, Comcast, Proton, Zoho, Fastmail, Proofpoint, and Mimecast are also recognized. AOL, AT&T-hosted, and Verizon legacy MX routes normalize to the shared Yahoo infrastructure policy so they cannot probe the same provider concurrently under different labels. The existing provider strategies remain responsible for interpreting SMTP evidence. Provider policies add process-wide concurrency, minimum intervals, bounded retries, and policy-block cooldowns without replacing domain pacing. After a policy cooldown, one half-open probe decides whether the provider resumes or returns to cooldown. Google `421`/`451` responses with `4.7.x` enhanced codes are treated as rate-limited, inconclusive evidence rather than mailbox rejection. Gateway acceptance is recorded separately from strong mailbox evidence. SMTP results include a complete command-stage session, basic and enhanced status codes, normalized category, text classification, failed stage, MX, provider, attempt, timing, banner/EHLO/TLS diagnostics, and sanitized response excerpts. A recipient rejection is definitive only when session evidence proves that `MAIL FROM` succeeded and `RCPT TO` returned a recipient-specific permanent rejection.

An in-memory observation store records non-sensitive domain/provider behavior such as catch-all outcomes, gateway acceptance, verification blocking, rate limiting, greylisting probability, and temporary failures. The abstraction is replaceable by Redis, SQL, or telemetry-backed history later. It never stores the target address as domain-level history.

Address intelligence is intentionally separate from domain intelligence. Conservative typo suggestions and free-provider detection work locally. Toxic-domain, known spam-trap, abuse-risk, suppression, and MX-forward results require explicitly configured application-owned intelligence. Alias, alternate-address, and domain-age contracts are available, but the default providers return `Unknown` because SMTP and DNS cannot establish those facts reliably.

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
- standard structured logging levels.

## Tests

```bash
dotnet test EmailValidation.sln
```

Normal tests require no public network. To enable the separate live DNS test:

```bash
EMAIL_VALIDATION_RUN_LIVE_TESTS=1 dotnet test tests/EmailValidation.IntegrationTests
```

## Prototype limitations

- Many providers deliberately obscure recipient existence, accept all recipients, greylist, or block port 25. Those outcomes are `Unknown`/`Risky`, not false `Invalid` claims.
- Domains without an explicit MX can use RFC implicit A/AAAA fallback. Results expose this as `usedImplicitMxFallback` and `ImplicitMxFallback`; configuring an explicit MX is strongly preferable operationally.
- Catch-all acceptance is modeled as likely evidence, never proof.
- Absence from a local disposable, toxic, trap, or abuse list is not represented as authoritative proof that an address is clean.
- Domain age, aliases, and alternate addresses remain `Unknown` until a reliable provider is registered.
- Provider strategies interpret acceptance conservatively: Google Workspace, Microsoft 365, Proofpoint, and Mimecast acceptance can represent gateway-level rather than mailbox-level acceptance.
- The in-memory cache and throttles are process-local abstractions intended to be replaced for a distributed deployment.
- This phase intentionally contains no hosted API, persistence, queues, proxy/IP rotation, tenant/authentication, or commercial validation-provider integration.
