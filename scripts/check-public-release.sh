#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$ROOT"

if ! command -v rg >/dev/null 2>&1; then
  printf 'error: ripgrep (rg) is required for the public-release scan.\n' >&2
  exit 1
fi

RG_BASE=(
  --hidden --no-messages --line-number --color never
  --glob '!.git/**'
  --glob '!artifacts/**'
  --glob '!**/bin/**'
  --glob '!**/obj/**'
  --glob '!**/__pycache__/**'
  --glob '!TestResults/**'
)

PUBLIC_PATHS=(
  README.md README.zh-CN.md CONTRIBUTING.md SECURITY.md CHANGELOG.md
  docs scripts src .github
)

failed=0

check_all() {
  local label="$1"
  local pattern="$2"
  local output
  output="$(rg "${RG_BASE[@]}" --pcre2 -- "$pattern" . || true)"
  if [[ -n "$output" ]]; then
    printf '\n[%s]\n%s\n' "$label" "$output" >&2
    failed=1
  fi
}

check_public_sources() {
  local label="$1"
  local pattern="$2"
  local output
  output="$(rg "${RG_BASE[@]}" --pcre2 -- "$pattern" "${PUBLIC_PATHS[@]}" 2>/dev/null || true)"
  if [[ -n "$output" ]]; then
    printf '\n[%s]\n%s\n' "$label" "$output" >&2
    failed=1
  fi
}

check_all 'Private-key material' '-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----'
check_all 'Known credential format' '(?:tskey-(?:auth|client|api)-[A-Za-z0-9_-]{16,}|github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})'
check_all 'Likely hard-coded secret' '(?i)(?:password|passwd|client_secret|access_token|api_key)\s*[:=]\s*["'"'][^"'"'\r\n]{8,}["'"']'
check_all 'Machine-specific Linux home path' '(?<![A-Za-z0-9_])/home/[A-Za-z0-9._-]+/'
check_all 'Machine-specific Windows profile path' '(?i)[A-Z]:\\Users\\(?!Public(?:\\|$)|Default(?:\\|$))[^%<>{}\\/\s]+\\'
check_public_sources 'Generated Tailscale tailnet suffix' '(?i)\btail[a-f0-9]{6,}\.ts\.net\b'
check_public_sources 'Embedded valid Ed25519 public key' 'ssh-ed25519\s+AAAAC3NzaC1lZDI1NTE5AAAAI[A-Za-z0-9+/]{42}[A-Za-z0-9+/=]'

tailscale_ips="$(rg "${RG_BASE[@]}" --pcre2 -- '(?<![0-9])100\.(?:6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])(?:\.[0-9]{1,3}){2}(?![0-9])' "${PUBLIC_PATHS[@]}" 2>/dev/null || true)"
tailscale_ips="$(printf '%s\n' "$tailscale_ips" | grep -Ev '100\.64\.0\.(0|10)(/10)?' || true)"
if [[ -n "$tailscale_ips" ]]; then
  printf '\n[Unexpected literal Tailscale IPv4]\n%s\n' "$tailscale_ips" >&2
  failed=1
fi

while IFS= read -r path; do
  [[ -z "$path" ]] && continue
  case "$path" in
    ./artifacts/*|./.git/*|*/bin/*|*/obj/*|*/__pycache__/*) continue ;;
  esac
  printf '\n[Runtime or credential file present] %s\n' "$path" >&2
  failed=1
done < <(find . -type f \( -name 'config.json' -o -name 'known_hosts' -o -name '*.pem' -o -name '*.pfx' -o -name 'id_rsa' -o -name 'id_ed25519' \) -print)

if (( failed != 0 )); then
  printf '\nPublic-release scan FAILED. Remove or replace every reported value before publishing.\n' >&2
  exit 1
fi

printf 'Public-release scan passed: no common credentials or machine-specific identity found.\n'
