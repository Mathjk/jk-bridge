#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd -- "$ROOT"

if ! command -v rg >/dev/null 2>&1; then
  printf 'error: ripgrep (rg) is required for the documentation-link check.\n' >&2
  exit 1
fi

mapfile -t DOC_FILES < <(
  printf '%s\n' README.md README.zh-CN.md CONTRIBUTING.md SECURITY.md
  find docs -type f -name '*.md' -print | sort
  find packaging -type f -name '*.md' -print 2>/dev/null | sort
)

failed=0

check_target() {
  local file="$1"
  local raw="$2"
  local target="$raw"

  target="${target#<}"
  target="${target%>}"
  case "$target" in
    ''|'#'*|http://*|https://*|mailto:*) return ;;
  esac

  target="${target%%#*}"
  target="${target%%\?*}"
  if [[ -n "$target" && ! -e "$(dirname -- "$file")/$target" ]]; then
    printf 'broken local link: %s -> %s\n' "$file" "$raw" >&2
    failed=1
  fi
}

while IFS=: read -r file target; do
  [[ -n "$file" && -n "$target" ]] && check_target "$file" "$target"
done < <(rg --with-filename --no-line-number --only-matching \
  --replace '$1' '\]\(([^)[:space:]]+)' "${DOC_FILES[@]}" || true)

while IFS=: read -r file target; do
  [[ -n "$file" && -n "$target" ]] && check_target "$file" "$target"
done < <(rg --with-filename --no-line-number --only-matching \
  --replace '$1' '(?:src|href)="([^"]+)"' "${DOC_FILES[@]}" || true)

if (( failed != 0 )); then
  exit 1
fi

printf 'Documentation link check passed: all relative links and images exist.\n'
