#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
DELIVERY="${1:-}"

SOURCE_FILES=(
  scripts/jkbridge_agent.py
  scripts/jkbridge_relay.py
  packaging/linux/jkbridge-agentctl
  packaging/linux/jkbridge-agent.service
  packaging/linux/jkbridge-agent.env.example
  packaging/linux/README.md
  docs/AGENT_PROTOCOL.md
  docs/AGENT_THREAT_MODEL.md
)

failed=0

for relative in "${SOURCE_FILES[@]}"; do
  if [[ ! -s "$ROOT/$relative" ]]; then
    printf 'missing Agent package source: %s\n' "$relative" >&2
    failed=1
  fi
done

if (( failed != 0 )); then
  exit 1
fi

if ! command -v rg >/dev/null 2>&1; then
  printf 'error: ripgrep (rg) is required for the Agent package check.\n' >&2
  exit 1
fi

python3 -m py_compile \
  "$ROOT/scripts/jkbridge-linux.py" \
  "$ROOT/scripts/jkbridge_agent.py" \
  "$ROOT/scripts/jkbridge_relay.py"
bash -n "$ROOT/packaging/linux/jkbridge-agentctl"

help_output="$(python3 "$ROOT/scripts/jkbridge_agent.py" --help)"
for command in bootstrap recovery-code serve status; do
  if [[ "$help_output" != *"$command"* ]]; then
    printf 'Agent help does not expose required lifecycle command: %s\n' "$command" >&2
    failed=1
  fi
done

controller_help="$("$ROOT/packaging/linux/jkbridge-agentctl" --help)"
for command in preflight install upgrade rollback uninstall start stop status fallback; do
  if [[ "$controller_help" != *"$command"* ]]; then
    printf 'Agent controller help does not expose required lifecycle command: %s\n' "$command" >&2
    failed=1
  fi
done

unit="$ROOT/packaging/linux/jkbridge-agent.service"
environment_example="$ROOT/packaging/linux/jkbridge-agent.env.example"

if ! rg --quiet -- '^ExecStart=.*jkbridge_agent\.py.*serve(?:[[:space:]]|$)' "$unit"; then
  printf 'Agent unit must start jkbridge_agent.py in serve mode.\n' >&2
  failed=1
fi

unsafe_unit="$(rg --line-number --pcre2 -- '(?i)(?:^|[[:space:]])(?:User=root|sudo(?:[[:space:]]|$)|0\.0\.0\.0|\[?::\]?)' "$unit" || true)"
if [[ -n "$unsafe_unit" ]]; then
  printf 'Agent unit contains a root, sudo, or wildcard-listener directive:\n%s\n' "$unsafe_unit" >&2
  failed=1
fi

literal_secret="$(rg --line-number --pcre2 -- '(?i)^[[:space:]]*(?:[A-Z0-9_]*(?:TOKEN|PASSWORD|CREDENTIAL|RECOVERY|SECRET)[A-Z0-9_]*)=[^[:space:]#]+' "$environment_example" || true)"
if [[ -n "$literal_secret" ]]; then
  printf 'Agent environment example must not contain a literal credential:\n%s\n' "$literal_secret" >&2
  failed=1
fi

if [[ -n "$DELIVERY" ]]; then
  if [[ ! -d "$DELIVERY" ]]; then
    printf 'Agent delivery directory does not exist: %s\n' "$DELIVERY" >&2
    exit 1
  fi

  DELIVERY_FILES=(
    jkbridge_agent.py
    jkbridge_relay.py
    linux/jkbridge-agentctl
    linux/jkbridge-agent.service
    linux/jkbridge-agent.env.example
    linux/README.md
    docs/AGENT_PROTOCOL.md
    docs/AGENT_THREAT_MODEL.md
  )

  for relative in "${DELIVERY_FILES[@]}"; do
    if [[ ! -s "$DELIVERY/$relative" ]]; then
      printf 'missing Agent delivery asset: %s\n' "$relative" >&2
      failed=1
    fi
  done

  for executable in jkbridge_agent.py jkbridge_relay.py linux/jkbridge-agentctl; do
    if [[ -f "$DELIVERY/$executable" && ! -x "$DELIVERY/$executable" ]]; then
      printf 'Packaged %s must be executable.\n' "$executable" >&2
      failed=1
    fi
  done

  if find "$DELIVERY" -type l -print -quit | grep -q .; then
    printf 'Agent delivery tree must not contain symbolic links.\n' >&2
    failed=1
  fi

  runtime_files="$(find "$DELIVERY" -type f \( \
    -name 'state.json' -o \
    -name 'state.json.*' -o \
    -name '.transaction.json' -o \
    -name 'devices.json' -o \
    -name 'credentials.json' -o \
    -name 'bootstrap.json' -o \
    -name 'recovery.json' -o \
    -name 'server-secret' -o \
    -name 'server-secret.*' \
  \) -print)"
  if [[ -n "$runtime_files" ]]; then
    printf 'Agent delivery contains runtime state or credentials:\n%s\n' "$runtime_files" >&2
    failed=1
  fi

  embedded_secret="$(rg \
    --hidden \
    --text \
    --no-messages \
    --line-number \
    --pcre2 \
    -- "(?:-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----|jkbridge://[^[:space:]\"'<>]+\\?token=[A-Za-z0-9_-]{32,}|\"(?:bootstrapCredential|deviceCredential|enrollmentCredential|pairingToken|recoveryCode)\"\\s*:\\s*\"[A-Za-z0-9_-]{24,}\")" \
    "$DELIVERY" || true)"
  if [[ -n "$embedded_secret" ]]; then
    printf 'Agent delivery contains embedded private, pairing, recovery, or device material:\n%s\n' "$embedded_secret" >&2
    failed=1
  fi
fi

if (( failed != 0 )); then
  exit 1
fi

printf 'Agent package check passed: sources and delivery metadata are non-secret and least-privilege.\n'
