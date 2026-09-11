#!/usr/bin/env bash

# Passive OWASP ZAP scan of a running DocBook; pass a target to scan somewhere other than localhost.

set -euo pipefail

TARGET="${1:-http://host.docker.internal:5205}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Override with the Docker Hub mirror, zaproxy/zap-stable, when ghcr.io is unreachable.
IMAGE="${ZAP_IMAGE:-ghcr.io/zaproxy/zaproxy:stable}"

echo "Scanning ${TARGET}"

# Exit code 2 means warnings were raised, which is a finished scan rather than a failed one.
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "${HERE}:/zap/wrk:rw" \
  "${IMAGE}" \
  zap-baseline.py \
  -t "${TARGET}" \
  -r zap-report.html \
  -w zap-report.md \
  -I \
  || [ "$?" -eq 2 ]

echo "Wrote ${HERE}/zap-report.html"
