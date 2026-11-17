#!/usr/bin/env bash
# SessionStart: puts the project's live state into context, so a session does not begin with "read LESSONS.md"
# or with a stale belief about what is finished. Read-only; never fails the session.
cd "${CLAUDE_PROJECT_DIR:-$(pwd)}" 2>/dev/null || exit 0

{
  echo "## Zanzibar project state at session start"
  echo
  echo "Git: $(git branch --show-current 2>/dev/null) @ $(git log -1 --format='%h %s' 2>/dev/null)"
  changes="$(git status --short 2>/dev/null | head -15)"
  if [[ -n "$changes" ]]; then echo "Uncommitted changes:"; printf '%s\n' "$changes"; else echo "Working tree clean."; fi
  echo
  echo "Open PRD deviations (DECISIONS.md rows marked Blocked / Not verified / Open; phases they touch are NOT accepted):"
  open="$(grep -E '^\| D[0-9]+ ' DECISIONS.md 2>/dev/null | grep -E '\*\*(Blocked|Not verified|Open)' | cut -d'|' -f2,3 | sed 's/^ */  - /')"
  if [[ -n "$open" ]]; then printf '%s\n' "$open"; else echo "  - none"; fi
  echo
  if [[ -f .verify/last-pass ]] && [[ "$(cat .verify/last-pass)" == "$(scripts/worktree-fingerprint.sh 2>/dev/null)" ]]; then
    echo "verify.sh: last pass matches the current working tree."
  else
    echo "verify.sh: no pass recorded for the current working tree."
  fi
  leftovers="$(pgrep -af '^[^ ]*spicedb serve|^[^ ]*(Poc\.Api|Poc\.DevIssuer)( |$)|^[^ ]*dotnet .*(Poc\.Api|Poc\.DevIssuer)\.dll' 2>/dev/null | cut -c1-140)"
  ports="$(ss -ltnpH 2>/dev/null | grep -E ':(5080|5090|5173|50051|50061|9091|9092) ' | awk '{print "  " $4 "  " $6}')"
  if [[ -n "$leftovers$ports" ]]; then
    echo "Already running (stop by PID before tests; never pkill -f):"
    [[ -n "$leftovers" ]] && printf '  %s\n' "$leftovers"
    [[ -n "$ports" ]] && printf '%s\n' "$ports"
  else
    echo "No project processes or ports in use."
  fi
} > /tmp/.zanzibar-session-status.$$ 2>/dev/null

jq -n --rawfile ctx /tmp/.zanzibar-session-status.$$ \
  '{hookSpecificOutput: {hookEventName: "SessionStart", additionalContext: $ctx}}' 2>/dev/null
rm -f /tmp/.zanzibar-session-status.$$
exit 0
