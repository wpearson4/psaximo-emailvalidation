#!/usr/bin/env bash
set -euo pipefail

deployment_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$deployment_directory"

if [[ -z "${LETSENCRYPT_EMAIL:-}" ]]; then
    echo "Set LETSENCRYPT_EMAIL to the Let's Encrypt account contact address." >&2
    exit 2
fi

certbot_arguments=(
    certonly
    --standalone
    --preferred-challenges http
    --non-interactive
    --agree-tos
    --no-eff-email
    --keep-until-expiring
    --email "$LETSENCRYPT_EMAIL"
    --domain email.digitalwarehouse.io
)

if [[ "${LETSENCRYPT_STAGING:-false}" == "true" ]]; then
    certbot_arguments+=(--staging)
fi

docker compose stop nginx certbot >/dev/null 2>&1 || true
docker compose run --rm --no-deps --entrypoint certbot certbot "${certbot_arguments[@]}"
docker compose up -d nginx certbot

echo "Let's Encrypt certificate installed for email.digitalwarehouse.io."
