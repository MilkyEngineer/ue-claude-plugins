---
name: unreal-agent-workflow
description: How a lead agent runs a multi-agent Unreal Engine project with UnrealAgentKit - tiered workers (ue-low, ue-medium, ue-high, ue-xhigh), a read-only reviewer (ue-review), milestone plans with epic briefs (E1.md, E2.md...), delegation and relaying reports, adversarial reviews, spec-doc sync, restart recovery, and an opt-in usage-limit watchdog. Use it when planning or running a milestone with several agents, spawning or resuming ue-* agents, writing epic briefs, running a review, or recovering after a usage limit or a Claude Code restart.
---

# Running an Unreal project with agents

You are the lead. You plan, split work into epics, spawn workers, relay their requests, run reviews, and keep the docs in sync. Workers do the work. The ground rules they share (the `uak` lock, detached runs, no spawning, briefs, reports) are in their agent definitions, so spawn prompts carry only the task.

Every run goes through `uak`, the kit's CLI. If `uak` is not found, publish it first: see `${CLAUDE_PLUGIN_ROOT}/docs/INSTALL.md`. Check the setup with `uak env`, and see every command's options with `uak help`.

- `uak build` and `uak test -filter=<prefix> [-gpu] -name=<unique>` take the editor lock themselves.
- `uak lock run -name=<unique> -- <command...>` holds the lock around any other editor run; `uak lock status` shows the holder and the queue.
- `uak runs start -name=<unique> -owner=<E# or lead> -- <command...>` starts a long run detached; `uak runs list [-all]` follows runs, and `uak runs adopt -pid=<PID> -name= -owner=` records one started some other way.
- A `.ps1` command given to `uak lock run` or `uak runs start` runs with `-ExecutionPolicy Bypass`, so pass only scripts the project trusts.
- `uak compile <file>... [-dependents]` checks single files without the lock.
- `uak vcs status|changed|revision` reports version control (Git, Perforce or none).

## 1. Pick a tier

| Agent | Model | Use for | Default cap |
| --- | --- | --- | --- |
| `ue-low` | Sonnet | running builds, tests and verifications; reading logs; babysitting long runs; syncing docs; obvious fixes | 4 |
| `ue-medium` | inherit | a bounded feature slice in one module with its tests; a contained bug fix | 2 |
| `ue-high` | inherit | multi-file or cross-module work; tricky bugs; performance work; epic-sized tasks | 2 |
| `ue-xhigh` | inherit | formats and contracts; correctness-critical algorithms; deep engine investigations | 1 |
| `ue-review` | inherit, read-only | adversarial and independent reviews | 1 |

- **Caps** limit how many of each run at once. There is no overall cap. They are defaults: the user may change them (memory, RAM and cost decide), and a project's CLAUDE.md may record its own.
- **Before spawning,** count the live agents by tier. Pick the lowest tier that can do the task well.
- **Read the first few ue-low reports closely,** and do failure triage yourself: Sonnet may misread a failure.

## 2. Plan a milestone: plan, briefs README, one brief per epic

- **The plan** (for example `Plans/M3-Plan.md`) holds goals, epics, shared contracts and exit criteria. It wins over a brief where they disagree.
- **The briefs folder** (for example `Plans/M3/`) holds a `README.md` and one `E<n>.md` per epic. Start from [templates/README.md](templates/README.md) and [templates/E.md](templates/E.md).
  - The README holds the file-ownership map, the shared facts (test data, verification commands, current cache or format versions) and the gotchas everyone has hit.
  - A brief holds scope, what it owns and must not touch, contracts and dependencies, spec sections, done criteria, and then **Current state**, **Next step** and **Gotchas**.
- **Ownership:** you edit scope, ownership and done criteria. Only the epic's agent edits its own Current state, Next step and Gotchas.
- **Spawn prompts stay short:** "You are E4. Read `Plans/M3/README.md`, then `Plans/M3/E4.md`, and do its next step." Choose the tier by the next step, not the epic: one epic can move between tiers.
- **Why briefs, not an agent type per epic:** briefs are versioned, survive restarts and compaction, and have one owner. Per-epic agent types fix one effort level and add a copy of the plan that drifts.

## 3. Delegate and relay

- **Workers ask you** with SendMessage to "main". Answer promptly. Decisions that belong to the owner go to the owner.
- **Low-worker requests.** For long, low-judgment work (chains of runs, babysitting, re-running jobs stuck on the lock), a worker asks you for a ue-low. Spawn it within the caps with the requester's exact run list. Relay its report to the requester with SendMessage.
- **When a report arrives:**
  - check that the brief's Current state and Next step were updated;
  - check claims against evidence (run names, logs, test counts) before you repeat them;
  - relay what other epics need;
  - note any files touched outside the owner's area.
- **Surface permission denials.** A worker that reports a denied tool call must not have worked around it. Tell the user what was denied and why it was needed. Never widen permissions to get past a denial without the user.

## 4. Review adversarially

- **At each milestone's end,** and after risky changes, spawn `ue-review`. Give it the scope (the diff, epic or area), the project's worst failure class, the spec sections, and where earlier review records live.
- **It cannot edit.** It reports ranked findings, with evidence and proposed fixes, separating confirmed from plausible. You write the review record (for example `Reviews/M3-Review.md`), numbering findings as earlier records do.
- **Send fixes to the owning epics** as normal tasks. Re-review the fixes for anything in the worst failure class.

## 5. Keep the spec doc in sync

If the project mirrors its spec in a separate document (for example a shared doc for the owner), workers quote each changed spec paragraph in full in their reports. Apply those quotes to the mirror in the same session, section by section. Don't paraphrase them. When a report lacks the quotes, ask for them.

## 6. Survive usage limits and restarts

- **Usage limits.** Every agent stops at a usage limit. The `autoContinueAtUsageLimit` setting may not wake the session.
  - **Auto-resume is opt-in.** It is the project owner's choice, and it is off unless they turned it on. They turn it on by telling you, or with a line in the project's CLAUDE.md such as "Auto-resume: allowed". Without that, keep no watchdog: when agents stop at a limit, tell the user, and resume only when they ask.
  - **If the project's owner has allowed auto-resume,** keep a watchdog while agents are working: CronCreate, hourly, session-only, with a prompt like the one below. Crons die with the session and expire after 7 days, so recreate one that is missing.

    > Watchdog: check each agent spawned this session. For each that stopped at a usage limit or a restart, resume it with SendMessage: tell it to re-check any half-written files, rerun what it was waiting on, and carry on with its next step. Then run `uak runs list` and `uak lock status`, and resume any lead task that was interrupted.
- **After a Claude Code restart:**
  - Agents do not restart themselves. They come back as stopped. Resume each one with SendMessage, and tell it to rerun what it was waiting on.
  - Background shells are dead, and they also die after 2 hours. Long runs must go through `uak runs start`, which survives both; follow them with `uak runs list`. A run started some other way can be recorded with `uak runs adopt`.
  - The watchdog, if auto-resume is allowed, is gone. Recreate it.
- **Auto-compaction.** Long lead sessions compact. Keep decisions and state in files (plans, briefs, review records, the project's CLAUDE.md or memory), not only in the conversation. See `${CLAUDE_PLUGIN_ROOT}/docs/SETTINGS.md` for the threshold.

## 7. Optional: pass, then commit, then continue

With the user's agreement, run milestones without checking in:
1. When the milestone's final verification and review pass, commit its changes (never push unless asked).
2. Go straight on to the next milestone's plan and briefs.
3. On a failure, fix it (or send it to the owning epic), re-run, and follow the same loop.
4. Interrupt the user only for a critical question. Use a push notification if one is set up, rather than waiting silently. Record every other decision in the spec, plan or review docs.

Without that agreement, stop at each milestone boundary and report.
