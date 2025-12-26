#!/usr/bin/env bash

set -euo pipefail

APP_IMAGE="${APP_IMAGE:?APP_IMAGE must be set}"
ENVIRONMENT="${ENVIRONMENT:-staging}"
DEPLOY_DIR="${DEPLOY_DIR:-/opt/xrengine}"
COMPOSE_FILE="${COMPOSE_FILE:-${DEPLOY_DIR}/docker-compose.yml}"
STACK_NAME="${STACK_NAME:-xrengine}"
ENV_FILE="${ENV_FILE:-${DEPLOY_DIR}/.env}"
ACTIVE_SLOT_FILE="${ACTIVE_SLOT_FILE:-${DEPLOY_DIR}/.active_slot}"
PREVIOUS_IMAGE_FILE="${PREVIOUS_IMAGE_FILE:-${DEPLOY_DIR}/.previous_image}"
CURRENT_IMAGE_FILE="${CURRENT_IMAGE_FILE:-${DEPLOY_DIR}/.current_image}"
NGINX_UPSTREAM_FILE="${NGINX_UPSTREAM_FILE:-/etc/nginx/conf.d/active_upstream.conf}"
SERVER_A_PORT="${SERVER_A_PORT:-5001}"
SERVER_B_PORT="${SERVER_B_PORT:-5002}"
STOP_OLD_SLOT="${STOP_OLD_SLOT:-true}"
HEALTH_PATH="${HEALTH_PATH:-/healthz}"
PUBLIC_BASEURL="${PUBLIC_BASEURL:-}"

log() { echo "[$(date -u +%Y-%m-%dT%H:%M:%SZ)] $*"; }

require_files() {
  for file in "$@"; do
    if [[ ! -f "$file" ]]; then
      log "Missing required file: $file"
      exit 1
    fi
  done
}

write_upstream() {
  local slot="$1"
  echo "set \$active_upstream server_${slot};" | sudo tee "$NGINX_UPSTREAM_FILE" >/dev/null
  sudo nginx -s reload
}

health_check() {
  local port="$1"
  local attempts=30
  local delay=5
  local url="http://127.0.0.1:${port}${HEALTH_PATH}"
  for ((i=1;i<=attempts;i++)); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      log "Health check succeeded on port ${port}"
      return 0
    fi
    log "Waiting for healthy response from ${url} (attempt ${i}/${attempts})"
    sleep "$delay"
  done
  log "Service on port ${port} failed health checks"
  return 1
}

active_slot="a"
if [[ -f "$ACTIVE_SLOT_FILE" ]]; then
  active_slot="$(cat "$ACTIVE_SLOT_FILE" | tr -d '[:space:]')"
fi

inactive_slot="b"
inactive_port="$SERVER_B_PORT"
if [[ "$active_slot" == "b" ]]; then
  inactive_slot="a"
  inactive_port="$SERVER_A_PORT"
fi

log "Current active slot: ${active_slot}. Deploying to inactive slot: ${inactive_slot} on port ${inactive_port}"

require_files "$COMPOSE_FILE"
mkdir -p "$DEPLOY_DIR"
touch "$ENV_FILE"

if [[ -f "$CURRENT_IMAGE_FILE" ]]; then
  cp "$CURRENT_IMAGE_FILE" "$PREVIOUS_IMAGE_FILE"
fi
echo "$APP_IMAGE" > "$CURRENT_IMAGE_FILE"

export SERVER_IMAGE="$APP_IMAGE"
export ASPNETCORE_ENVIRONMENT="$ENVIRONMENT"
export PUBLIC_BASEURL
export SERVER_A_PORT
export SERVER_B_PORT
export ENV_FILE

sudo docker compose -p "$STACK_NAME" -f "$COMPOSE_FILE" pull "server_${inactive_slot}"
sudo docker compose -p "$STACK_NAME" -f "$COMPOSE_FILE" up -d "server_${inactive_slot}"

health_check "$inactive_port"

write_upstream "$inactive_slot"
echo "$inactive_slot" > "$ACTIVE_SLOT_FILE"

if [[ "${STOP_OLD_SLOT,,}" == "true" ]]; then
  log "Stopping old slot server_${active_slot}"
  sudo docker compose -p "$STACK_NAME" -f "$COMPOSE_FILE" stop "server_${active_slot}" || true
fi

health_check "$inactive_port"

if [[ -n "$PUBLIC_BASEURL" ]]; then
  public_url="${PUBLIC_BASEURL%/}${HEALTH_PATH}"
  log "Verifying public endpoint at ${public_url}"
  if ! curl -fsS "$public_url" >/dev/null 2>&1; then
    log "WARNING: public health check failed for ${public_url}"
  fi
fi

log "Deployment complete. Active slot is now ${inactive_slot}"
