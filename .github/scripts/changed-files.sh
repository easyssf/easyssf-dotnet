#!/usr/bin/env bash
# Classifies the files a push or pull request changes, for the jobs of ci.yml:
#
#   changed-files.sh <base> <head>     the commits base..head (three-dot: since their merge base)
#   changed-files.sh                   everything, for a manual run or an unknown range
#
# Prints "code=true|false" and "docs=true|false" in the format of GITHUB_OUTPUT. A file that is
# not documentation is code, so an unknown file runs the full build.
set -euo pipefail

is_doc() {
  case "$1" in
    *.md | LICENSE | NOTICE | .gitignore | .gitattributes | .lychee.toml | _typos.toml) return 0 ;;
    .idea/* | .run/* | scratch/* | .github/dependabot.yml | .github/rulesets/*) return 0 ;;
    # .editorconfig stays code, the build checks the formatting with it
    *) return 1 ;;
  esac
}

code=false
docs=false
if [ $# -eq 2 ] && git rev-parse --verify --quiet "$1^{commit}" >/dev/null \
    && git rev-parse --verify --quiet "$2^{commit}" >/dev/null; then
  files=$(git diff --name-only "$1...$2")
  if [ -z "$files" ]; then
    echo "no files changed between $1 and $2" >&2
  fi
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    if is_doc "$file"; then
      case "$file" in *.md) docs=true ;; esac
    else
      code=true
    fi
  done <<< "$files"
else
  echo "no commit range, assuming everything changed" >&2
  code=true
  docs=true
fi
echo "code=$code"
echo "docs=$docs"
