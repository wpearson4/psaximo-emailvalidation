#!/usr/bin/env bash
set -euo pipefail

endpoint="${ELASTICSEARCH_ENDPOINT:?set ELASTICSEARCH_ENDPOINT}"
environment="${EMAIL_VALIDATION_ENVIRONMENT:?set EMAIL_VALIDATION_ENVIRONMENT}"
stream="email-validation-observations-${environment}-v1"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
curl_args=(--fail-with-body --silent --show-error)
if [[ -n "${ELASTICSEARCH_API_KEY:-}" ]]; then
  curl_args+=(-H "Authorization: ApiKey ${ELASTICSEARCH_API_KEY}")
elif [[ -n "${ELASTICSEARCH_USERNAME:-}" ]]; then
  curl_args+=(-u "${ELASTICSEARCH_USERNAME}:${ELASTICSEARCH_PASSWORD:?set ELASTICSEARCH_PASSWORD}")
fi

version="$(curl "${curl_args[@]}" "${endpoint}/" | jq -r '.version.number')"
major="${version%%.*}"
if (( major < 8 )); then
  echo "Elasticsearch ${version} is unsupported; version 8 or newer is required." >&2
  exit 1
fi

if [[ "${1:-}" != "--apply" ]]; then
  curl "${curl_args[@]}" "${endpoint}/_cluster/health" | jq '{cluster_name,status,number_of_nodes,number_of_data_nodes}'
  curl "${curl_args[@]}" "${endpoint}/_ilm/policy/email-validation-observations-v1" >/dev/null || true
  curl "${curl_args[@]}" "${endpoint}/_component_template/email-validation-observations-v1-mappings" >/dev/null || true
  curl "${curl_args[@]}" "${endpoint}/_index_template/email-validation-observations-v1" >/dev/null || true
  echo "Validation only. Re-run with --apply to install artifacts and create ${stream}."
  exit 0
fi

curl "${curl_args[@]}" -X PUT -H 'Content-Type: application/json' \
  "${endpoint}/_ilm/policy/email-validation-observations-v1" \
  --data-binary "@${script_dir}/lifecycle-policy.json"
curl "${curl_args[@]}" -X PUT -H 'Content-Type: application/json' \
  "${endpoint}/_component_template/email-validation-observations-v1-mappings" \
  --data-binary "@${script_dir}/component-template-mappings.json"
curl "${curl_args[@]}" -X PUT -H 'Content-Type: application/json' \
  "${endpoint}/_component_template/email-validation-observations-v1-settings" \
  --data-binary "@${script_dir}/component-template-settings.json"
curl "${curl_args[@]}" -X PUT -H 'Content-Type: application/json' \
  "${endpoint}/_index_template/email-validation-observations-v1" \
  --data-binary "@${script_dir}/index-template.json"
curl "${curl_args[@]}" -X PUT "${endpoint}/_data_stream/${stream}"
curl "${curl_args[@]}" "${endpoint}/_data_stream/${stream}" | jq .
