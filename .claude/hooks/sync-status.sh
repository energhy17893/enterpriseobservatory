#!/usr/bin/env bash
# Reports, at session start, anything that is not on GitHub yet.
#
# It deliberately does NOT push. Pushing publishes work, and a hook that
# published whatever happened to be lying around could put a half-finished
# state in front of a reviewer with no way to take it back. The job here is
# to make unsynced work impossible to overlook; deciding to send it stays a
# decision somebody makes on purpose.
#
# It must never fail a session. Every step is best-effort and the script
# always exits 0.

set +e

ROOT=$(git rev-parse --show-toplevel 2>/dev/null)
[ -z "$ROOT" ] && exit 0
cd "$ROOT" || exit 0

command -v python >/dev/null 2>&1 || exit 0

# Best effort, and bounded: an unreachable remote must not hold the session
# open. Without this the whole point is lost -- a fetch that hangs is exactly
# the case where you most want to be told the remote is unreachable.
REMOTE_OK=no
if timeout 15 git fetch --quiet origin 2>/dev/null; then
  REMOTE_OK=yes
fi

DIRTY=$(git status --porcelain 2>/dev/null | wc -l | tr -d ' ')
BRANCH=$(git branch --show-current 2>/dev/null)

# Every local branch whose tip is on NO remote branch at all.
#
# Deliberately not "ahead of its own upstream". While a pull request is open,
# main sits ahead of origin/main even though every one of those commits is
# already on GitHub on the PR branch -- and a hook that reported that every
# session would be crying wolf about work that is perfectly safe. A warning
# that is usually wrong is one nobody reads, which is how the real one gets
# missed. So the question asked here is the one that actually matters: is
# this commit anywhere other than this machine?
UNPUSHED=""
for ref in $(git for-each-ref --format='%(refname:short)' refs/heads/ 2>/dev/null | grep -v '^worktree-agent-'); do
  tip=$(git rev-parse "$ref" 2>/dev/null) || continue
  if [ -z "$(git branch -r --contains "$tip" 2>/dev/null)" ]; then
    UNPUSHED="$UNPUSHED $ref"
  fi
done
UNPUSHED=$(echo "$UNPUSHED" | sed 's/^ *//')

GH_AUTH=no
if command -v gh >/dev/null 2>&1; then
  gh auth status >/dev/null 2>&1 && GH_AUTH=yes
elif [ -x "/c/Program Files/GitHub CLI/gh.exe" ]; then
  "/c/Program Files/GitHub CLI/gh.exe" auth status >/dev/null 2>&1 && GH_AUTH=yes
else
  GH_AUTH=missing
fi

REMOTE_OK="$REMOTE_OK" DIRTY="$DIRTY" BRANCH="$BRANCH" \
UNPUSHED="$UNPUSHED" GH_AUTH="$GH_AUTH" python - <<'PY'
import json, os

remote = os.environ.get("REMOTE_OK", "no")
dirty = int(os.environ.get("DIRTY") or 0)
branch = os.environ.get("BRANCH") or "(detached)"
unpushed = [b for b in (os.environ.get("UNPUSHED") or "").split() if b]
gh = os.environ.get("GH_AUTH", "no")

notes = []

if remote != "yes":
    notes.append(
        "origin is NOT reachable (fetch failed or timed out), so everything "
        "below is measured against a possibly stale view of the remote."
    )

if unpushed:
    notes.append(
        "Branches with commits that are not on origin: " + ", ".join(unpushed) +
        ". Offer to push them; do not push without saying so first."
    )

if dirty:
    notes.append(
        f"{dirty} uncommitted change(s) in the working tree on '{branch}'. "
        "Check whether they are real work or leftovers before doing anything else."
    )

if gh == "missing":
    notes.append("The gh CLI is not installed, so pull requests cannot be opened from here.")
elif gh == "no":
    notes.append(
        "gh is installed but not authenticated ('gh auth login' is interactive, "
        "so the user has to run it). Pull requests cannot be opened until they do."
    )

if not notes:
    print(json.dumps({"suppressOutput": True}))
else:
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "SessionStart",
            "additionalContext":
                "Git sync status at session start:\n- " + "\n- ".join(notes) +
                "\n\nTell the user what is unsynced and offer to sync it. "
                "Do not push, open a pull request, or discard anything without "
                "their go-ahead."
        },
        "suppressOutput": True,
    }))
PY

exit 0
