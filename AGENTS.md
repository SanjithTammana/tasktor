# For coding agents (Codex and others)

Read `CLAUDE.md` first: the same rules apply to every agent (the specs win, tests first, `scripts/check.sh` green before
each commit, no network in the agent, personal data in `Sensitive<T>` and never logged, generic only). This file adds how
agents coordinate, so the owner does not have to carry messages between them.

## Lanes

| Lane | Agent | Branch | Where it runs |
|---|---|---|---|
| main | Claude (cloud) | `claude/cool-euler-qjz6mk` | Linux; Windows CI checks the build |
| capture | Codex (owner's PC) | `codex/m12-capture-adapters` | Windows; real-PC checks |

The capture lane owns `src/Tasktor.Capture/**` adapters and wiring plus their tests; anything else is "outside the lane".

## The loop (capture lane)

At the start of every session, and again before stopping:

1. `git fetch origin` and merge `origin/claude/cool-euler-qjz6mk` into your branch. Run the full suite on that baseline.
   A failure caused only by the environment is a blocker to report, not a reason to stop.
2. **Read your tasks**: open GitHub issues labelled `lane:codex`, oldest first.
   - With the GitHub CLI: `gh issue list --label lane:codex --state open` and `gh issue view <n>`.
   - Without it (the repository is public): `curl -s "https://api.github.com/repos/SanjithTammana/tasktor/issues?labels=lane:codex&state=open"`.
   An issue is the request; its newest comments may change it. If an issue conflicts with a spec, the spec wins: say so.
3. Work them in small tested commits. Mention the issue in the commit message (`capture: … (#12)`).
4. **Report** in `docs/lanes/codex-capture.md` (Done, Checks run, Interpretations, Needs Windows verification, Requests,
   Blockers), commit it, and **push**. The push is how the main lane hears from you: it watches the lane's pull request,
   so every push, CI result and comment reaches it without the owner.
5. To ask the main lane something, add it under **Requests** in your report and push; if `gh` is signed in, also comment
   on the lane's pull request. Never wait idle for an answer: continue with the next issue or record a blocker.
6. When an issue is done, say so in the report (and close it with `gh issue close <n> --comment "…"` if you can). The
   main lane closes it otherwise, after merging.

## What the main lane does

- Files each request for the capture lane as an issue labelled `lane:codex`.
- Watches the lane's pull request: reviews each push against the lane rules, merges into its branch when the review and
  Windows CI are clean, answers Requests (by fixing on its branch and saying which commit, or in the issue), closes issues.
- Never pushes to `codex/*` branches.

## Rules that are easy to miss

- Never skip, disable or loosen a test to get green; never change the owner's system configuration (keyboard layouts,
  security settings) to run a check: write a script the owner can run and record the check as theirs.
- Commits carry only the repository owner as author: no co-author trailers or tool attribution.
