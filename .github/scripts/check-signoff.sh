#!/usr/bin/env bash
# Checks that every commit of a pull request carries a Developer Certificate of Origin sign-off
# (a "Signed-off-by: Name <email>" trailer, git commit -s). Merge commits and the commits of
# GitHub's bots (Dependabot) are exempt.
#
#   check-signoff.sh <base> <head>
set -euo pipefail

base=$1
head=$2
missing=()
checked=0
for commit in $(git rev-list --no-merges "$base..$head"); do
  author=$(git log -1 --format='%ae' "$commit")
  case "$author" in
    *'[bot]@users.noreply.github.com') continue ;;
  esac
  checked=$((checked + 1))
  if ! git log -1 --format='%B' "$commit" | grep -Eq '^Signed-off-by: [^<]+ <[^@> ]+@[^> ]+>$'; then
    missing+=("$commit")
  fi
done

if [ ${#missing[@]} -eq 0 ]; then
  echo "all $checked commit(s) are signed off"
  exit 0
fi

echo "::error::${#missing[@]} commit(s) without a Signed-off-by trailer:"
for commit in "${missing[@]}"; do
  git log -1 --format='  %h %s (%an <%ae>)' "$commit"
done
cat <<MSG

Every commit needs a Developer Certificate of Origin sign-off (see AGENTS.md). Sign off the
commits of this branch and force-push it:

  git rebase --signoff $base
  git push --force-with-lease
MSG
exit 1
