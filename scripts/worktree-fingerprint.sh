#!/usr/bin/env bash
# Prints one hash for the state of the working tree: HEAD, every tracked change (staged or not) and every untracked file
# that is not ignored. verify.sh records it on success; the commit gate (.claude/hooks/bash-guard.py) compares against it.
# Staging does not change it, so `git add` after a green verify.sh still matches.
set -euo pipefail
cd "$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)"
{
  git rev-parse HEAD
  git diff HEAD --binary
  git ls-files -z --others --exclude-standard | sort -z |
    while IFS= read -r -d '' f; do printf '%s %s\n' "$(git hash-object -- "$f")" "$f"; done
} | sha256sum | cut -d' ' -f1
