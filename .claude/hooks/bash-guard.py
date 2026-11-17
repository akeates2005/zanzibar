#!/usr/bin/env python3
"""PreToolUse guard for Bash. Each rule is a trap that cost real time in this repo (LESSONS.md section 5).

Denies, with the reason fed back to Claude:
  1. unanchored `pkill -f` / `pgrep -f`, which match the invoking shell (exit 144, twice);
  2. in-place `sed -i` / `perl -i` and heredoc writes into source directories (they damaged files silently);
  3. `dotnet test --no-build` not chained after a `dotnet build &&` (stale binaries after a failed build);
  4. `rm -rf` on a bare variable or on a system / home / parent path;
  5. `git commit` of anything but Markdown unless ./verify.sh last passed on exactly this working tree.
Anything it cannot parse is allowed: this is a seatbelt for known mistakes, not a sandbox.
"""
import json
import os
import re
import shlex
import subprocess
import sys

SOURCE_DIRS = ("src/", "tests/", "web/src/", "web/e2e/", "authz/")


def deny(reason: str) -> None:
    print(json.dumps({"hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": reason + " (zanzibar bash-guard; see CLAUDE.md)",
    }}))
    sys.exit(0)


def segments(cmd: str):
    """Split a command line into simple commands on ; & | and newlines that are not inside quotes.

    Quote-aware so a quoted regex alternation (`pgrep -af '^a|^b'`) stays one command. An unbalanced quote (say an
    apostrophe in a heredoc body) swallows the rest, which can only mean fewer checks, never a false refusal."""
    parts, current, quote, i = [], [], None, 0
    while i < len(cmd):
        ch = cmd[i]
        if quote:
            if ch == "\\" and quote == '"' and i + 1 < len(cmd):
                current.append(cmd[i:i + 2])
                i += 2
                continue
            if ch == quote:
                quote = None
        elif ch in "'\"":
            quote = ch
        elif ch == "\\" and i + 1 < len(cmd):
            current.append(cmd[i:i + 2])
            i += 2
            continue
        elif ch in ";&|\n":
            parts.append("".join(current))
            current = []
            i += 1
            continue
        current.append(ch)
        i += 1
    parts.append("".join(current))
    return [p.strip() for p in parts if p.strip()]


def words(seg: str):
    try:
        return shlex.split(seg, comments=False)
    except ValueError:
        return seg.split()


def check_pkill(seg: str):
    w = words(seg)
    if not w or os.path.basename(w[0]) not in ("pkill", "pgrep"):
        return
    flags = [x for x in w[1:] if x.startswith("-") and not x.startswith("--")]
    if not any("f" in f for f in flags):
        return
    patterns = [x for x in w[1:] if not x.startswith("-")]
    if patterns and not patterns[-1].startswith("^"):
        deny(f"`{w[0]} -f` with an unanchored pattern also matches the shell running this command and has killed it "
             "before (exit 144). Kill by PID, or anchor the pattern to the executable, e.g. '^[^ ]*spicedb serve'. "
             "The API and issuer run as Poc.Api / Poc.DevIssuer, not dotnet.")


def touches_source(text: str) -> bool:
    return any(re.search(r"(^|[\s'\"=/])" + re.escape(d), text) for d in SOURCE_DIRS)


def check_inplace_edit(seg: str):
    w = words(seg)
    if not w:
        return
    tool = os.path.basename(w[0])
    inplace = (tool == "sed" and any(x == "-i" or x.startswith("-i") or x == "--in-place" or
                                     (x.startswith("-") and not x.startswith("--") and "i" in x) for x in w[1:])) or \
              (tool == "perl" and any(x.startswith("-") and not x.startswith("--") and "i" in x for x in w[1:]))
    if inplace and any(touches_source(x) for x in w[1:] if not x.startswith("-")):
        deny(f"In-place `{tool}` edits of source files have silently failed or damaged files here. Use the Edit tool "
             "(or Write for a new file), then confirm the change before running tests.")


def check_heredoc_write(cmd: str):
    for m in re.finditer(r"\b(?:cat|tee)\s+(?:-a\s+)?>{0,2}\s*['\"]?([^\s'\"<>|;&]+)['\"]?\s*<<", cmd):
        if touches_source(m.group(1)):
            deny(f"Writing {m.group(1)} through a shell heredoc bypasses the edit tools. Use Write for a new file and "
                 "Edit for a change.")


def check_no_build(cmd: str):
    m = re.search(r"\bdotnet\s+test\b[^;&|\n]*--no-build", cmd)
    if not m:
        return
    if re.search(r"\bdotnet\s+build\b[^;|\n]*&&", cmd[: m.start()]):
        return
    deny("`dotnet test --no-build` runs the previous binary if the last build failed (an analyzer error once gave stale "
         "results here). Drop --no-build, or chain it as `dotnet build ... && dotnet test ... --no-build`.")


DANGEROUS_RM = re.compile(r"^(/|/\*|~|~/.*|\$HOME.*|\$\{HOME\}.*|\.|\.\.|\.\./.*|\*|/(bin|boot|dev|etc|lib|lib64|"
                          r"opt|proc|root|sbin|sys|usr|var|home)(/.*)?)$")


def check_rm(seg: str):
    w = words(seg)
    if not w or os.path.basename(w[0]) != "rm":
        return
    flags = "".join(x for x in w[1:] if x.startswith("-") and not x.startswith("--"))
    if not ("r" in flags.lower() and "f" in flags):
        return
    raw = re.split(r"\s+", seg.strip())[1:]
    for target in [x for x in raw if not x.startswith("-")]:
        bare = target.strip("'\"")
        if DANGEROUS_RM.match(bare):
            deny(f"`rm -rf {bare}` targets a system, home or parent path. Name the exact directory to remove.")
        if re.match(r"^\$[A-Za-z_]\w*(/.*)?$", bare) or re.match(r"^\$\{[A-Za-z_]\w*\}(/.*)?$", bare):
            deny(f"`rm -rf {target}` expands to the wrong path if the variable is empty or unset. "
                 "Use ${VAR:?} so an empty value aborts, e.g. rm -rf \"${CLONE:?}\".")


def check_commit(cmd: str, cwd: str):
    if not re.search(r"\bgit\s+(?:-C\s+\S+\s+)?commit\b", cmd):
        return
    root = os.environ.get("CLAUDE_PROJECT_DIR") or cwd
    try:
        top = subprocess.run(["git", "-C", root, "rev-parse", "--show-toplevel"], capture_output=True, text=True,
                             check=True).stdout.strip()
        changed = subprocess.run(["git", "-C", top, "diff", "HEAD", "--name-only"], capture_output=True, text=True,
                                 check=True).stdout.split()
        changed += subprocess.run(["git", "-C", top, "ls-files", "--others", "--exclude-standard"],
                                  capture_output=True, text=True, check=True).stdout.split()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return
    if not changed or all(f.endswith(".md") for f in changed):
        return  # documentation-only commits need no gate
    marker = os.path.join(top, ".verify", "last-pass")
    try:
        recorded = open(marker, encoding="utf-8").read().strip()
    except OSError:
        recorded = ""
    current = subprocess.run([os.path.join(top, "scripts", "worktree-fingerprint.sh")], capture_output=True,
                             text=True).stdout.strip()
    if not recorded or recorded != current:
        deny("Commit blocked: ./verify.sh has not passed on this exact working tree (it records .verify/last-pass on "
             "success, and any edit since then invalidates it). Run ./verify.sh in the background, wait for it, then "
             "commit with its summary in the message (PRD rule 7). Documentation-only (*.md) commits are exempt.")


def main():
    try:
        data = json.load(sys.stdin)
    except json.JSONDecodeError:
        sys.exit(0)
    if data.get("tool_name") != "Bash":
        sys.exit(0)
    cmd = (data.get("tool_input") or {}).get("command") or ""
    for seg in segments(cmd):
        check_pkill(seg)
        check_inplace_edit(seg)
        check_rm(seg)
    check_heredoc_write(cmd)
    check_no_build(cmd)
    check_commit(cmd, data.get("cwd") or os.getcwd())
    sys.exit(0)


if __name__ == "__main__":
    main()
