# Assessment verification record

Baseline commit: `3c5a1ccb62ea1f4da4fb1ba85a7f3f8bfa10e404`. Date: October 9, 2026. Runtime: macOS arm64, .NET SDK 10.0.301. Scope: offline repository review and isolated tests. No live SMTP, production data inspection or production code changes.

## Existing suites

Commands used `dotnet test <project> --no-restore -m:1 -p:UseSharedCompilation=false --logger 'trx;LogFileName=<suite>.trx' --results-directory /private/tmp/emailvalidation-assessment-tests`.

| Project path under repository | Result | Reported test duration |
| --- | --- | --- |
| `tests/EmailValidation.Core.Tests/EmailValidation.Core.Tests.csproj` | 572 passed, 0 failed, 0 skipped | 4 seconds |
| `tests/EmailValidation.Api.Tests/EmailValidation.Api.Tests.csproj` | 52 passed, 0 failed, 0 skipped | 1 second |
| `tests/EmailValidation.Grpc.Tests/EmailValidation.Grpc.Tests.csproj` | 2 passed, 0 failed, 0 skipped | 27 milliseconds |

Initial sandboxed Core execution failed because MSBuild could not bind local IPC sockets. The isolated suites then ran successfully with local socket permission. No test failure is concealed by this tooling failure. Integration tests were not run; no credential/environment secret was read for them. Raw TRX outputs remain in the temporary results directory; the durable report stores aggregate results only.

## Synthetic reproduction output

The [harness source](Harness.cs) invokes existing built components using synthetic reserved-domain addresses, fake persistence and memory streams. It does not open a network connection. Private parser/lifetime calls use reflection solely to characterize the baseline; those checks will need updating if internals change.

```text
DNS timeout composition: details=NoMailRouting; reasons=DnsTimeout,NoMailRouting; retry=False
Temporary retry: delaySeconds=5; cachedResultReusable=True; remainingSeconds=115
Confirmed accept-all plan: controls=False; mailbox=True; reusedCatchAll=False
65 ASCII local octets accepted=True; 66 UTF8 local octets accepted=True
Mixed multiline reply parsed=SmtpResponse { Code = 250, Text = 250-first line | 550 5.1.1 User unknown }
Domain lifetime: absentTTL=1.00:00:00; zeroTTL=1.00:00:00; 30secondTTL=00:05:00
Cache store renews evidence expiry bySeconds=3540
Case distinct local parts share flight: factoryCalls=1
Mixed ordinary and Null MX: explicitNullMx=True
```

These are observed component behaviors. The retry reproduction proves the schedule/reuse policy mismatch, not the incidence of production retry loss. The DNS composition exercises evaluator/retry integration with a synthetic timeout; the implicit-fallback transient SocketException path was source-reviewed, not live-tested. The mixed reply test proves parser acceptance, not a successful full malformed SMTP exchange. The cache test demonstrates renewal from storage time; actual prevalence depends on update patterns. No before/after improvement is measured.

## Reproduction

After building the source assemblies using the Core test command above, run the [harness project](Harness.csproj):

```sh
dotnet run --project docs/assessments/2026-10-09/evidence/Harness.csproj -m:1 -p:UseSharedCompilation=false
```

The project references built Debug/net10.0 assemblies in the repository, uses the shared framework and adds no package dependency. All inputs are synthetic. Builds generate ignored bin/obj artifacts only; the harness is assessment evidence rather than production code. It reports the old behavior and intentionally is not an acceptance test for the proposed fixes.

## Findings not reproduced against external infrastructure

- Concurrent Revalidating execution, domain-write races, stale cross-process cache reads, broker duplicate-detection deferral loss and abandoned-worker recovery require dedicated Mongo/Service Bus tests.
- Provider verification restrictions, TLS coverage gain, probe-pattern bias, real catch-all classification accuracy and block-rate reductions require authorized representative evidence.
- Production configuration, enabled rollout modes, queue properties, source identity health, actual retention rules outside this repository and infrastructure costs were not established.

## Primary reference scope

SMTP syntax/case/protocol recommendations use [RFC 5321](https://www.rfc-editor.org/rfc/rfc5321.html); Null MX uses [RFC 7505](https://www.rfc-editor.org/rfc/rfc7505.html); TLS uses [RFC 3207](https://www.rfc-editor.org/rfc/rfc3207.html); negative caching uses [RFC 2308](https://www.rfc-editor.org/rfc/rfc2308.html). These specifications support protocol semantics, not the platform’s accuracy.

Provider examples use [Microsoft DBEB](https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/use-directory-based-edge-blocking), [Google SMTP errors](https://knowledge.workspace.google.com/admin/support/troubleshooting/gmail-smtp-errors-and-codes), and [Yahoo SMTP errors](https://senders.yahooinc.com/smtp-error-codes/). Service Bus behavior uses [Microsoft duplicate detection documentation](https://learn.microsoft.com/en-us/azure/service-bus-messaging/duplicate-detection). Competitor documentation is used only for capability comparison, never as evidence of comparative accuracy.
