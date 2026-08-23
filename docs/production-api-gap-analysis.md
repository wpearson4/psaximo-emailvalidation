# Production API discovery and gap matrix

This matrix records repository state before the production API work. It is based on the complete solution inventory rather than on a parallel design.

| Capability | Existing implementation at discovery | Initial status | Required change |
|---|---|---|---|
| .NET/runtime | All projects targeted .NET 10 | IMPLEMENTED | Preserve .NET 10. |
| Domain/Application/Core | Domain, Application, and shared Core validation pipeline | IMPLEMENTED | Reuse without transport dependencies. |
| Infrastructure | MongoDB Driver 2.30 stores, Service Bus retry/job dispatch, Elasticsearch sender source | IMPLEMENTED | Reuse and retain MongoDB 4.4-compatible operations. |
| Console/Worker | Console and durable Service Bus worker call the shared validator | IMPLEMENTED | Keep unchanged. |
| REST host | Minimal prototype with `/v1/email/validate` and direct Domain result serialization | PARTIALLY IMPLEMENTED | Correct routes, explicit v1 DTOs, metadata, security, errors, limits, ownership, and observability. |
| Validation status | Canonical lifecycle query and a status route | PARTIALLY IMPLEMENTED | Add versioned DTO mapping, scope, and ownership enforcement. |
| Jobs | Application job service, Mongo stores, Service Bus dispatcher, worker, and prototype routes | PARTIALLY IMPLEMENTED | Correct routes, pagination envelope, scopes, ownership, and durable idempotency. |
| gRPC status | Separate gRPC executable with unary status and server streaming; late subscribers already received canonical state | PARTIALLY IMPLEMENTED | Host in the primary API, add OAuth/scopes, retain contract and stream semantics. |
| gRPC validation | No unary validation/read service | NOT IMPLEMENTED | Add `emailvalidation.v1` mapped to the shared validator/status query. |
| Authentication | No API authentication | NOT IMPLEMENTED | Standard OIDC/JWT validation for issuer, audience, signature, and lifetime. |
| Authorization | Unrestricted default validation access; no route scopes | IMPLEMENTED BUT INCORRECT | Central named scopes and a default-deny policy. |
| Tenant isolation | Validation access seam existed, but was unrestricted; jobs had no equivalent | PARTIALLY IMPLEMENTED | Persist consumer grants and enforce them for REST and gRPC. |
| Idempotency | Durable queue message IDs existed, but consumer retries could create another job | PARTIALLY IMPLEMENTED | Principal-scoped, Mongo-authoritative `Idempotency-Key` reservation. |
| Problem Details | Basic Problem Details registration | PARTIALLY IMPLEMENTED | Add trace IDs, structured status pages, and sanitized public errors. |
| OpenAPI/Swagger | None | NOT IMPLEMENTED | Generated v1 OpenAPI, OAuth scopes, stable operation IDs, protected UI, build export. |
| Health | None | NOT IMPLEMENTED | Minimal liveness and dependency-aware readiness. |
| API limits | A status-stream concurrency limiter only | PARTIALLY IMPLEMENTED | Consumer rate limits, bounded bodies, stream concurrency, explicit CORS allowlist. |
| Observability | Structured logging plus Core/Job/Status meters | PARTIALLY IMPLEMENTED | Add trace scopes and route/status metrics without payload/token labels. |
| HTTP protocols/TLS | Separate HTTP REST and HTTP/2 gRPC hosts | IMPLEMENTED BUT INCORRECT | One host with explicit HTTP/1 and HTTP/2 ingress ports behind production TLS. |
| Azure configuration | App Configuration, Key Vault references, `DefaultAzureCredential` | PARTIALLY IMPLEMENTED | Reuse and add a Docker-secret bootstrap; no interactive production login. |
| Docker/Compose | No API artifacts | NOT IMPLEMENTED | Multi-stage non-root image, health check, AlmaLinux deployment. |
| Mongo host networking | No container decision; host loopback would fail from a bridge | NOT IMPLEMENTED | Linux host networking keeps loopback Mongo private without publishing 27017. |
| Tests | REST prototype and gRPC mapper tests | PARTIALLY IMPLEMENTED | Add REST/gRPC security, ownership, streaming, OpenAPI, health, and idempotency tests. |

The finished design converts `EmailValidation.Grpc` from a second executable into a transport assembly loaded by `EmailValidation.Api`. It introduces no second validator, job processor, lifecycle store, retry system, or provider policy.
