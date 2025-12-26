#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
VERSION_FILE="${REPO_ROOT}/version/version.json"
PYTHON_BIN="${PYTHON_BIN:-python3}"

if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
  PYTHON_BIN="python"
fi

usage() {
  cat <<'USAGE'
Compute a repository-wide semantic version based on the configured base version
and the requested release channel.

Options:
  --channel <name>     Channel to use (nightly, beta, release, dev). If omitted,
                       the branch name is inspected.
  --branch <name>      Branch name to use when inferring the channel.
  --run-number <num>   Build/run number used in prerelease identifiers.
  --date <YYYYMMDD>    Date stamp for nightly builds. Defaults to current UTC date.

Environment:
  GITHUB_REF_NAME, GITHUB_BASE_REF, GITHUB_RUN_NUMBER can be used instead of
  flags when running inside GitHub Actions.
USAGE
}

CHANNEL=""
BRANCH_NAME="${GITHUB_REF_NAME:-${GITHUB_BASE_REF:-}}"
RUN_NUMBER="${GITHUB_RUN_NUMBER:-0}"
DATE_STAMP="$(date -u +%Y%m%d)"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --channel)
      CHANNEL="$2"
      shift 2
      ;;
    --branch)
      BRANCH_NAME="$2"
      shift 2
      ;;
    --run-number)
      RUN_NUMBER="$2"
      shift 2
      ;;
    --date)
      DATE_STAMP="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ ! -f "$VERSION_FILE" ]]; then
  echo "Missing version source at ${VERSION_FILE}" >&2
  exit 1
fi

BASE_VERSION="$("$PYTHON_BIN" - <<'PY'
import json, pathlib, sys
version_path = pathlib.Path(sys.argv[1])
data = json.loads(version_path.read_text())
print(f"{data['major']}.{data['minor']}.{data['patch']}")
PY
"$VERSION_FILE")"

if [[ -z "$BASE_VERSION" ]]; then
  echo "Failed to compute base version" >&2
  exit 1
fi

if [[ -z "$CHANNEL" ]]; then
  case "$BRANCH_NAME" in
    nightly|refs/heads/nightly) CHANNEL="nightly" ;;
    beta|refs/heads/beta) CHANNEL="beta" ;;
    release|refs/heads/release) CHANNEL="release" ;;
    *) CHANNEL="dev" ;;
  esac
fi

case "$CHANNEL" in
  nightly)
    VERSION="${BASE_VERSION}-nightly.${DATE_STAMP}.${RUN_NUMBER}"
    ;;
  beta)
    VERSION="${BASE_VERSION}-beta.${RUN_NUMBER}"
    ;;
  release)
    VERSION="${BASE_VERSION}"
    ;;
  dev)
    VERSION="${BASE_VERSION}-dev.${RUN_NUMBER}"
    ;;
  *)
    echo "Unsupported channel '${CHANNEL}'" >&2
    exit 1
    ;;
esac

if [[ -z "$VERSION" ]]; then
  echo "Failed to compute version" >&2
  exit 1
fi

echo "$VERSION"
