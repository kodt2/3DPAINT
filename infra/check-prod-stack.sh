#!/usr/bin/env bash
# check-prod-stack.sh — validates the production compose profile before and
# after the 3d cleanup (step 3d.0, smoke suite).
#
# It resolves `infra/compose.yml` + `infra/compose.https.yml` (profile "https")
# with `docker compose config --format json` and asserts:
#
#   1. Ports: only Caddy is published on an external (non-loopback) address,
#      and every Caddy external port sits on BIND_ADDR. Gateway/SeaweedFS keep
#      loopback-only ports.
#   2. Monolith: the `web` service publishes no ports in the production
#      profile (the only exception is the e2e stand, infra/compose.e2e.yml).
#   3. Origins: no dev/e2e origins (Vite 5173, e2e 18090) in PUBLIC_ORIGIN or
#      S3_CORS_ORIGINS.
#   4. TLS_MODE=prod: PUBLIC_ORIGIN and S3_CORS_ORIGINS must not contain
#      localhost, 127.0.0.1 or http:// (the script fails on their presence).
#
# Usage:
#   infra/check-prod-stack.sh [OPTIONS]
#
# Options:
#   --bind ADDR        Caddy host address (default: 0.0.0.0 — LAN/public)
#   --origin URL       PUBLIC_ORIGIN (default: https://3dpaint.example.com)
#   --s3-origin URL    S3_CORS_ORIGINS (default: https://3dpaint.example.com)
#   --s3-endpoint URL  PUBLIC_S3_ENDPOINT (default: https://s3.3dpaint.example.com)
#   --tls MODE         lan | prod (default: prod)
#   -h, --help         show this help
#
# Exit codes: 0 = all checks passed, 1 = a check failed, 2 = usage/env error.
set -euo pipefail

BIND_ADDR="0.0.0.0"
PUBLIC_ORIGIN="https://3dpaint.example.com"
S3_CORS_ORIGINS="https://3dpaint.example.com"
PUBLIC_S3_ENDPOINT="https://s3.3dpaint.example.com"
TLS_MODE="prod"
# Ports that only exist for local development / the e2e stand and must never
# appear in a production origin list.
DEV_PORTS=("5173" "18090")

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bind) BIND_ADDR="${2-}"; shift 2 ;;
    --origin) PUBLIC_ORIGIN="${2-}"; shift 2 ;;
    --s3-origin) S3_CORS_ORIGINS="${2-}"; shift 2 ;;
    --s3-endpoint) PUBLIC_S3_ENDPOINT="${2-}"; shift 2 ;;
    --tls) TLS_MODE="${2-}"; shift 2 ;;
    -h|--help) sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $1 (see --help)" >&2; exit 2 ;;
  esac
done

# An empty value means "do not override": the resolved compose default is used,
# which is exactly the case we want to catch when a default leaks a dev origin
# (localhost / Vite 5173) into production.

if ! command -v docker >/dev/null 2>&1; then
  echo "ERROR: docker is required" >&2; exit 2
fi
if ! docker compose version >/dev/null 2>&1; then
  echo "ERROR: docker compose plugin is required" >&2; exit 2
fi
if ! command -v jq >/dev/null 2>&1; then
  echo "ERROR: jq is required" >&2; exit 2
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

if [[ "$TLS_MODE" != "lan" && "$TLS_MODE" != "prod" ]]; then
  echo "ERROR: --tls must be 'lan' or 'prod' (got: $TLS_MODE)" >&2; exit 2
fi

# Derive single hostnames for Caddy's site blocks (cosmetic for this check).
host_of() { printf '%s' "${1#https://}" | cut -d: -f1; }

ENV_ARGS=("BIND_ADDR=$BIND_ADDR" "TLS_MODE=$TLS_MODE")
[[ -n "$PUBLIC_ORIGIN" ]] && ENV_ARGS+=("PUBLIC_ORIGIN=$PUBLIC_ORIGIN")
[[ -n "$S3_CORS_ORIGINS" ]] && ENV_ARGS+=("S3_CORS_ORIGINS=$S3_CORS_ORIGINS")
[[ -n "$PUBLIC_S3_ENDPOINT" ]] && ENV_ARGS+=("PUBLIC_S3_ENDPOINT=$PUBLIC_S3_ENDPOINT")
ENV_ARGS+=("MAIN_HOST=$(host_of "$PUBLIC_ORIGIN")")
ENV_ARGS+=("S3_HOST=$(host_of "$PUBLIC_S3_ENDPOINT")")
ENV_ARGS+=("ACME_EMAIL=admin@$(host_of "$PUBLIC_ORIGIN")")

CONFIG_JSON="$(
  env "${ENV_ARGS[@]}" \
    docker compose -f "$ROOT/infra/compose.yml" -f "$ROOT/infra/compose.https.yml" \
      --profile https config --format json
)" || {
  echo "ERROR: docker compose config failed" >&2; exit 2;
}

FAIL=0
ok()  { printf 'ok   %s\n' "$*"; }
bad() { printf 'FAIL %s\n' "$*"; FAIL=1; }

echo "== Production stack check (bind=$BIND_ADDR tls=$TLS_MODE) =="

# --- 1. Ports: only Caddy is external, on BIND_ADDR --------------------------
# External = host_ip not in the loopback set (empty/0.0.0.0/LAN IP are external).
EXTERNAL_PORTS="$(
  printf '%s' "$CONFIG_JSON" | jq -r '
    .services | to_entries[]
    | .key as $svc
    | (.value.ports // [])[]
    | select(((.host_ip // "") | IN("127.0.0.1", "::1", "localhost")) | not)
    | [$svc, (.host_ip // "<all>"), (.published|tostring), (.target|tostring)] | @tsv
  '
)"
if [[ -z "$EXTERNAL_PORTS" ]]; then
  ok "no externally published ports (BIND_ADDR is loopback)"
else
  while IFS=$'\t' read -r svc hip pub tgt; do
    if [[ "$svc" == "caddy" ]]; then
      if [[ "$hip" == "$BIND_ADDR" || "$hip" == "<all>" && "$BIND_ADDR" == "0.0.0.0" ]]; then
        ok "caddy publishes $pub->$tgt on $hip (BIND_ADDR)"
      else
        bad "caddy external port $pub->$tgt is on $hip, expected $BIND_ADDR"
      fi
    else
      bad "service '$svc' publishes port $pub->$tgt externally (only Caddy may)"
    fi
  done <<< "$EXTERNAL_PORTS"
fi

# --- 2. Monolith (web) must not publish ports in the production profile ------
WEB_PORTS="$(printf '%s' "$CONFIG_JSON" | jq '.services.web.ports // [] | length')"
if [[ "$WEB_PORTS" == "0" ]]; then
  ok "monolith (web) publishes no ports in the production profile"
else
  bad "monolith (web) publishes $WEB_PORTS port(s) in the production profile"
fi

# --- Extract resolved origin values -----------------------------------------
GATEWAY_ORIGIN="$(printf '%s' "$CONFIG_JSON" | jq -r '.services.gateway.environment.PUBLIC_ORIGIN // ""')"
S3_ALLOWED="$(
  printf '%s' "$CONFIG_JSON" | jq -r '
    (.services.storage.command // [])
    | map(select(startswith("-s3.allowedOrigins=")))
    | first // ""
    | sub("-s3.allowedOrigins="; "")
  '
)"

# --- 3. No dev/e2e origins ----------------------------------------------------
has_port() { # $1=origin-list  $2=port
  printf '%s' "$1" | grep -Eq "[:.]${2}([/:,]|$)"
}
DEV_LEAK=""
for port in "${DEV_PORTS[@]}"; do
  if has_port "$GATEWAY_ORIGIN" "$port" || has_port "$S3_ALLOWED" "$port"; then
    DEV_LEAK="$DEV_LEAK $port"
  fi
done
if [[ -z "$DEV_LEAK" ]]; then
  ok "no dev/e2e origins (${DEV_PORTS[*]}) in PUBLIC_ORIGIN / S3_CORS_ORIGINS"
else
  bad "dev/e2e origin port(s)$DEV_LEAK present in PUBLIC_ORIGIN / S3_CORS_ORIGINS"
fi

# --- 4. TLS_MODE=prod: no localhost / 127.0.0.1 / http:// --------------------
if [[ "$TLS_MODE" == "prod" ]]; then
  FORBIDDEN=("localhost" "127.0.0.1" "http://")
  PROD_LEAK=""
  for token in "${FORBIDDEN[@]}"; do
    if printf '%s' "$GATEWAY_ORIGIN" | grep -Fq "$token" \
       || printf '%s' "$S3_ALLOWED" | grep -Fq "$token"; then
      PROD_LEAK="$PROD_LEAK \"$token\""
    fi
  done
  if [[ -z "$PROD_LEAK" ]]; then
    ok "TLS_MODE=prod: PUBLIC_ORIGIN / S3_CORS_ORIGINS free of localhost, 127.0.0.1, http://"
  else
    bad "TLS_MODE=prod: PUBLIC_ORIGIN / S3_CORS_ORIGINS contain$PROD_LEAK"
  fi
else
  ok "TLS_MODE=lan: localhost/http:// origin check skipped (lan mode)"
fi

echo
if [[ "$FAIL" == "0" ]]; then
  echo "RESULT: PASS"
else
  echo "RESULT: FAIL"
  exit 1
fi
