# EmailValidation.Api operations and deployment

## Public boundary

`EmailValidation.Api` is the single REST and gRPC composition host. REST uses `/v1`; protobuf packages use `emailvalidation.v1` and the existing `emailvalidation.status.v1`. Requests map into the existing validator, status query/subscription, and Application job services.

REST resources and scopes:

- `POST /v1/email-validations` — `emailvalidation.validate`
- `GET /v1/email-validations/{validationId}` — `emailvalidation.read`
- `POST /v1/email-validation-jobs` — `emailvalidation.jobs.write`
- `GET /v1/email-validation-jobs/{jobId}` — `emailvalidation.jobs.read`
- `GET /v1/email-validation-jobs/{jobId}/results?skip=0&take=100` — `emailvalidation.jobs.read`

gRPC methods and scopes:

- `emailvalidation.v1.EmailValidationService/ValidateEmail` — `emailvalidation.validate`
- `emailvalidation.v1.EmailValidationService/GetValidation` — `emailvalidation.read`
- `emailvalidation.status.v1.EmailValidationStatus/GetValidationStatus` — `emailvalidation.read`
- `emailvalidation.status.v1.EmailValidationStatus/WatchValidationStatus` — `emailvalidation.stream`

All business operations deny unauthenticated callers. Tokens must be issued by `Authentication:Authority`, target `Authentication:Audience`, and pass standard issuer, audience, signature, expiration, and not-before validation. OAuth client credentials is the normal server-to-server flow. Tokens carry space-delimited `scope` or `scp` claims. Persisted tenant/subject grants protect validation and job identifiers.

`Idempotency-Key` is optional on job creation. A key is scoped to the authenticated tenant, or subject when there is no tenant. The same body returns the original job; a different body returns `409`.

## Development and contracts

Provide non-secret settings through configuration and secrets through user secrets. Azure App Configuration is skipped only by the automated `Testing` environment.

The host exposes HTTP/1 REST/health on loopback port 8080 and cleartext HTTP/2 gRPC on loopback port 8081. The Compose stack terminates public HTTPS and HTTP/2 TLS at Nginx on ports 80/443 and obtains certificates for `email.digitalwarehouse.io` with Certbot and Let's Encrypt. Bearer tokens never cross the untrusted network in plaintext, and certificates are kept in persistent volumes rather than application images.

Swagger UI and JSON are available in Development. Outside Development they are absent by default. `Api:OpenApi:ExposeInProduction=true` exposes them only to `emailvalidation.admin`.

Generate the machine-readable contract with:

```bash
dotnet build src/EmailValidation.Api/EmailValidation.Api.csproj -p:GenerateOpenApi=true
```

The artifact is `openapi/emailvalidation-v1.json`; generation uses a non-networked Testing configuration and needs no production credentials.

For an interactive local Swagger UI in Rider, select the `SwaggerLocal` launch profile. The profile disables Azure App Configuration and durable infrastructure, supplies non-production validation placeholders, binds only to loopback, and opens `http://localhost:8080/swagger`. From a terminal, the equivalent command is:

```bash
dotnet run --project src/EmailValidation.Api/EmailValidation.Api.csproj --launch-profile SwaggerLocal
```

Swagger is anonymous only in Development. Business operations still require their documented OAuth scopes. `Azure:AppConfigurationEnabled` defaults to `true`; disable it only in an explicitly local profile such as `SwaggerLocal`.

## Configuration and secrets

Non-secret settings include `Azure__AppConfigurationEndpoint`, `Azure__AppConfigurationLabel`, `Authentication__Authority`, `Authentication__Audience`, `Api__OpenApi__ExposeInProduction`, `Api__Cors__AllowedOrigins__0`, `Api__Limits__*`, `Api__RateLimiting__*`, and `Kestrel__Endpoints__*`.

Existing App Configuration/Key Vault keys remain authoritative for MongoDB, Service Bus, Elasticsearch, and validation behavior. Compose mounts an App Configuration connection string at `/run/secrets/azure_app_configuration_connection_string` and a service-principal certificate at `/run/secrets/azure_client_certificate`. `DefaultAzureCredential` uses the non-secret tenant/client IDs and mounted certificate to resolve Key Vault references. This avoids production `az login` and keeps client secrets out of environment variables. Keep source secret files outside the repository.

## AlmaLinux 8 / 10.10.252.31 networking

A read-only host preflight was completed on 2026-08-23. The target is AlmaLinux 8.10 on x86-64 with Docker 29.7.2 and Docker Compose 5.5.0. Ports 80, 443, 8080, and 8081 were unused, and no nginx, httpd, HAProxy, or Caddy service was active. The production image was built on the host and passed isolated health, HTTP/2, non-root, hardening, authorization, and graceful-shutdown checks. The validation container was removed; the image remains tagged `emailvalidation-api:host-validation`.

Public DNS for `email.digitalwarehouse.io` resolved to `172.106.108.81` during that preflight, but a public port 80 connection timed out. Before requesting a certificate, route public TCP 80 and 443 for that address to `10.10.252.31` and permit both ports through every upstream firewall/NAT layer. HTTP-01 issuance cannot succeed until Let's Encrypt can reach port 80.

MongoDB 4.4.31 is active with authorization enabled. Contrary to the safer loopback-only assumption, the existing `mongod.conf` binds port 27017 to `0.0.0.0`; the host input policy was also observed as accepting traffic. Deployment of this API does not require changing that existing service and must not publish MongoDB through Docker. Review MongoDB network exposure separately with the host owner before changing its bind address or firewall rules.

To repeat the read-only preflight:

```bash
docker version
docker compose version
ip -brief address
ss -ltnp | grep -E ':(27017|8080|8081)\b'
sudo grep -E '^[[:space:]]*(bindIp|port):' /etc/mongod.conf
```

Compose deliberately uses Linux `network_mode: host`. MongoDB 4.4 can remain on host loopback, an existing `mongodb://127.0.0.1:27017` URI remains valid for the API, and Docker never publishes 27017. The API is non-root with all capabilities dropped. Do not change FCV, upgrade MongoDB, or expose its port. Used operations—find/replace, ordinary/partial indexes, and ping—are MongoDB 4.4 compatible.

The API and same-host Nginx gateway both use host networking. The safe default keeps Kestrel on loopback while Nginx alone owns public ports 80/443. Do not set `API_BIND_ADDRESS=10.10.252.31` for this topology.

## Azure Container Registry

The application image defaults to `acrpometadsiscussrch.azurecr.io/emailvalidation-api`. The registry is an existing Standard-tier Azure Container Registry in resource group `rg-p-ometa-dsi-scus`; its admin account remains disabled.

Build in Azure and store an immutable tag directly in the registry:

```bash
IMAGE_TAG="$(git rev-parse --short=12 HEAD)"
az acr build \
  --registry acrpometadsiscussrch \
  --image "emailvalidation-api:${IMAGE_TAG}" \
  --image emailvalidation-api:latest \
  .
```

Give the production host only pull access. Prefer an ACR scope-map token or service principal restricted to `repositories/emailvalidation-api/content/read`; do not enable the registry admin account. Authenticate Docker once using that pull identity, with the password supplied through standard input rather than command-line arguments.

## Let's Encrypt bootstrap and deployment

Create an uncommitted `.env` on the host containing the non-secret settings, selected `IMAGE_TAG`, and paths to the two source secret files. Confirm public ports 80 and 443 reach this host, authenticate Docker to ACR, and pull the application image:

```bash
docker compose config --quiet
docker compose pull emailvalidation-api nginx certbot
export LETSENCRYPT_EMAIL=operations@digitalwarehouse.io
./deploy/letsencrypt/bootstrap.sh
docker compose ps
curl --fail --silent http://127.0.0.1:8080/health/live
curl --fail --silent http://127.0.0.1:8080/health/ready
curl --fail --silent https://email.digitalwarehouse.io/health/ready
docker compose logs --tail=200 emailvalidation-api nginx certbot
```

Replace the example Let's Encrypt contact address with the monitored operational address. For a rate-limit-safe rehearsal, set `LETSENCRYPT_STAGING=true`; remove the staging certificate volume before the first production issuance because staging certificates are not trusted. The bootstrap script briefly reserves host port 80 with Certbot's standalone HTTP-01 server, then starts Nginx and the renewal service.

Certbot checks renewal twice daily with the shared webroot. Nginx reloads its certificate state every six hours, so renewed certificates are picked up without restarting the API. Named volumes `letsencrypt`, `certbot_www`, `certbot_lib`, and `certbot_logs` persist all ACME state. Back up the `letsencrypt` volume and never commit its contents.

TLS-fronted REST smoke test:

```bash
curl --fail --header "Authorization: Bearer ${ACCESS_TOKEN}" \
  --header 'Content-Type: application/json' \
  --data '{"email":"person@example.com"}' \
  https://email.digitalwarehouse.io/v1/email-validations
```

Generate gRPC clients from `src/EmailValidation.Grpc/Protos`. Reflection exists only in Development and requires admin. Liveness checks only the process. Readiness performs a bounded Mongo ping when configured. Anonymous health responses contain only aggregate status. `stop_grace_period` allows graceful ASP.NET Core shutdown.
