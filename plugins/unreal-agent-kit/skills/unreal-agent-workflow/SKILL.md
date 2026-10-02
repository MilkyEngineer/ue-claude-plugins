---
name: unreal-agent-workflow
description: How a lead agent runs a multi-agent Unreal Engine project with UnrealAgentKit - tiered workers (low, medium, high, xhigh), a runner that cannot edit (runner), a read-only researcher (research) and reviewer (review), an architect that drafts plans and briefs (architect), milestone plans with epic briefs (E1.md, E2.md...), delegation and relaying reports, adversarial reviews, spec-doc sync, restart recovery, and an opt-in usage-limit watchdog. Use it when planning or running a milestone with several agents, spawning, resuming or respawning the kit's agents, writing epic briefs, running a review, or recovering after a usage limit or a Claude Code restart.
---

# Running an Unreal project with agents

You are the lead. You plan, split work into epics, spawn workers, relay their requests, run reviews, and keep the docs in sync. Workers do the work. The ground rules they share (the `uak` lock, detached runs, no spawning, briefs, reports) are in their agent definitions, so spawn prompts carry only the task.

Every run goes through `uak`, the kit's CLI. If `uak` is not found, or says it isn't published, publish it first: see `${CLAUDE_PLUGIN_ROOT}/docs/INSTALL.md`. Check the setup with `uak env`, and see every command's options with `uak help`.

- `uak build` and `uak test -filter=<prefix> [-gpu | -windowed] -name=<unique>` take the editor lock themselves. `-windowed` runs the editor in a window, for tests that need a real viewport and Slate windows. More UBT or editor arguments go after `--`.
- `uak lock run -name=<unique> -- <command...>` holds the lock around any other editor run; `uak lock status` shows the holder and the queue.
- `uak runs start -name=<unique> -owner=<E# or lead> -- <command...>` starts a run that may take over an hour, detached, and keeps the machine awake while it goes (Windows; `-allow-sleep` opts out); `uak runs list [-all]` lists runs, `uak runs wait -name=<name> -timeout=<seconds>` waits for one to end, and `uak runs adopt -pid=<PID> -name= -owner=` records one started some other way.
- A `.ps1` command given to `uak lock run` or `uak runs start` runs with `-ExecutionPolicy Bypass`, so pass only scripts the project trusts.
- `uak compile <file>... [-dependents]` checks single files without the lock.
- `uak vcs status|changed|revision` reports version control (Git, Perforce or none). On Perforce, `uak vcs edit|add|reopen <file>... [-c=<CL>]`, `uak vcs change new -description=` and `uak vcs change describe -c= -description=` change this client's pending changelists, and `uak vcs shelve -c=<CL>` (or `uak vcs shelve <file>...`, into a new changelist) shelves. `-replace` deletes shelved files that are not opened in the changelist (uak refuses unless `-drop-unopened`): use it only when the user asked to drop them. None of them reverts or submits.
- `uak horde preflight -c=<shelved CL> [-shelve] [-wait -timeout=<s>]` starts a Horde preflight and prints its job id and URL (`-shelve` only on changelists the agent created; without it, an equal preflight still running is reported as "reused: <job>"); `uak horde job -id=<job> [-wait -timeout=<s>]` reports or waits for a job; `uak horde streams` lists streams and preflight templates; `uak horde templates [-json]` lists the workspace stream's preflight templates with their parameters and the saved build settings. `-autosubmit` makes Horde submit the change when the preflight succeeds: only when the user asked for it. Never shelve a change again (`uak vcs shelve -c=`, or `-shelve`) while an auto-submit preflight of it runs: Horde submits the change's current shelf when it succeeds, so a new shelf would be submitted unbuilt (uak refuses when it can check Horde, and warns when it can't). Horde exit codes: 0 succeeded, 1 failed, 2 usage error, 3 still running, 4 not signed in, 5 another error or a refusal, 6 build settings needed, 7 warnings.
- **Horde, first use.** The horde commands need the Horde server's URL. When one says "No Horde server is configured", ask the user for the URL (a worker asks you), then run `uak horde config -server=<url>` once; it is kept for every kit version.
- **Horde sign-in.** There is no separate login step. A horde command signs in silently when it can; otherwise it opens the Horde sign-in page in the user's browser and waits up to 10 minutes (`-login-timeout=<seconds>`, the real limit). So whoever runs one tells you first, and you tell the user in one line: "a Horde sign-in page may open; please sign in". Exit 4 means nobody signed in in time: tell the user, then run it again. `-no-login` (CI, scripts) never opens the page. On Windows, uak keeps the access token (encrypted for the user) until it nearly expires, so commands after a sign-in don't ask again; `uak horde logout` deletes it.
- **Horde build settings (exit 6).** A preflight builds with the build settings saved for its stream (template and parameters, per user and server). With none saved, it starts nothing, exits 6, and prints the templates and parameters as JSON (as `uak horde templates -json`). A worker sends that output to you and never asks the user itself. You ask the user with AskUserQuestion: first the template (recommend the stream default, `"default": true`), then the bool and list parameters, at most 4 questions per call, each with the template's default as the first, recommended option (`multiSelect` for lists that take several). A question holds at most 4 options: for a list with more choices, offer the default and the likeliest others (or group them, such as "all consoles"), and let the user name the rest through "Other"; for a single-choice list, at most one answer. A list with exactly 1 choice is a yes/no question. Ask about text parameters only if the user wants to set them. Then save the answers with `uak horde config -stream=<id> -template=<id> -param:<id>=<value> ...` (bool `true|false`, list choice ids comma-separated, text as is) and run the preflight again. When the user asks to change these defaults at any time, run `uak horde templates -json`, ask the same questions with the saved values (`"current"`) as the recommended options, and save. `-template=` and `-param:` on a preflight change one run only; `-use-template-defaults` (CI) skips the saved settings.
- **Horde job links.** An agent that starts a preflight sends you its "Job URL: ..." line (SendMessage to "main") as soon as it is printed, before it waits, and again with the result. Pass both on to the user. `uak horde config -open=never|created|finished|failed` can also open the job's page in the browser during `-wait` (default never).

## 1. Pick a tier

The kit's agents are named `unreal-agent-kit:<name>` (for example `unreal-agent-kit:review`); this skill uses the short names.

| Agent | Model | Use for | Default cap |
| --- | --- | --- | --- |
| `runner` | Sonnet, low, cannot edit | running a given list of builds, tests, verifications or benchmark chains; following long runs; reporting with evidence | 4 |
| `low` | Sonnet | small tasks that may need an edit: syncing docs, reading logs, obvious fixes with the runs that check them | 4 |
| `medium` | inherit | a bounded feature slice in one module with its tests; a contained bug fix | 2 |
| `high` | inherit | multi-file or cross-module work; tricky bugs; performance work; epic-sized tasks | 2 |
| `xhigh` | inherit | formats and contracts; correctness-critical algorithms; deep engine investigations | 1 |
| `research` | inherit, high, read-only | engine and codebase investigations, answered with `file:line` evidence | 2 |
| `review` | inherit, read-only | adversarial and independent reviews | 1 |
| `architect` | inherit, high, writes only plan and brief drafts | drafting a milestone's plan, briefs README and epic briefs | 1 |

- **Caps** limit how many of each run at once. There is no overall cap. They are defaults: the user may change them (memory, RAM and cost decide), and a project's CLAUDE.md may record its own.
- **Before spawning,** count the live agents by tier. Pick the lowest tier that can do the task well.
- **Read the first few low and runner reports closely,** and do failure triage yourself: Sonnet may misread a failure.
- **Why a runner that cannot edit:** a verification that "fixes" what it finds no longer verifies the tree it was given. A runner reports, and the requester decides.
- **Why a read-only researcher:** engine investigations are long and need no edits. Its reading never conflicts with workers' edits, and its `file:line` answer saves each worker repeating the search. Its experiments (`uak compile`, `uak test`) queue on the lock like any run, and a compile experiment needs a module to put a temporary file in: name one in the spawn prompt if it may need it.

## 2. Plan a milestone: plan, briefs README, one brief per epic

- **Drafting.** You may spawn `architect` to draft the plan, the briefs README and the epic briefs: name the files it may write, the milestone's goals and spec sections, and the project's worst failure class. It never edits code or runs anything, cites `file:line` for the engine behaviour it relies on (or marks it "unverified: needs research"), and hands back open decisions for the owner. Review its drafts yourself, and send them to `review` when the milestone is risky, before any worker sees them. Put data-format and cache-version decisions in the plan to `xhigh`, as before.
- **The plan** (for example `Plans/M3-Plan.md`) holds goals, epics, shared contracts and exit criteria. It wins over a brief where they disagree.
- **The briefs folder** (for example `Plans/M3/`) holds a `README.md` and one `E<n>.md` per epic. Start from [templates/README.md](templates/README.md) and [templates/E.md](templates/E.md).
  - The README holds the file-ownership map, the shared facts (test data, verification commands, current cache or format versions) and the gotchas everyone has hit.
  - A brief holds scope, what it owns and must not touch, contracts and dependencies, spec sections, done criteria, and then **Current state**, **Next step** and **Gotchas**.
- **Ownership:** you edit scope, ownership and done criteria. Only the epic's agent edits its own Current state, Next step and Gotchas.
- **Spawn prompts stay short:** "You are E4. Read `Plans/M3/README.md`, then `Plans/M3/E4.md`, and do its next step." Choose the tier by the next step, not the epic: one epic can move between tiers.
- **Why briefs, not an agent type per epic:** briefs are versioned, survive restarts and compaction, and have one owner. Per-epic agent types fix one effort level and add a copy of the plan that drifts.

## 3. Delegate and relay

- **Workers ask you** with SendMessage to "main". Answer promptly. Decisions that belong to the owner go to the owner.
- **Runner requests.** For long, low-judgment work (chains of runs, following long runs, re-running jobs stuck on the lock), a worker asks you for a runner. Spawn it within the caps with the requester's exact run list, in order, and what to report. Relay its report to the requester with SendMessage.
- **Research requests.** For a long engine investigation, a worker may ask you for a research agent. Spawn it with the exact question and what the answer is for, and relay the answer. Spawn one yourself before planning work that leans on an engine behaviour nobody has checked.
- **Never spend an agent on waiting.** To wait for a detached run, run `uak runs wait -name=<name> -timeout=<seconds>` in a background shell whose own time limit is longer than `-timeout` and within the shell's two-hour limit. It costs nothing while it waits, and its end wakes you: exit 0 means the run exited 0, 1 that it failed or ended with no exit code, and 3 that `-timeout` passed with the run still going, so start another wait. An agent that wakes up again and again to check costs a turn each time, and a large one costs more.
- **Waiting for a Horde job** works the same way: run `uak horde job -id=<id> -wait -timeout=<seconds>` in a background shell. It prints nothing while it waits and one short summary at the end (the result, the job's URL, and only the failing steps). It prints the job's URL first. Exit 0 succeeded, 1 failed or did not complete, 2 usage error, 3 still running after `-timeout` (wait again), 4 not signed in (the sign-in page timed out: tell the user, then wait again), 5 another error, 7 succeeded with warnings. A preflight also exits 6 when build settings are needed (above). For a job longer than two hours, run the wait under `uak runs start`.
- **When a report arrives:**
  - check that the brief's Current state and Next step were updated;
  - check claims against evidence (run names, logs, test counts) before you repeat them;
  - relay what other epics need;
  - note any files touched outside the owner's area.
- **Surface permission denials.** A worker that reports a denied tool call must not have worked around it. Tell the user what was denied and why it was needed. Never widen permissions to get past a denial without the user.

## 4. Resume or respawn

You can't see how full an agent's context is, and you can't compact or clear it. What you see is each stop's notification: tokens used, tool calls, duration. The brief gives you a clean alternative: an agent that has handed back has written its state there, so a new agent can carry on from the brief alone.

- **Resume** (SendMessage to the stopped agent) when the next step is short and leans on context the brief doesn't hold: a debugging session halfway through, a fix half applied, an answer it is waiting for.
- **Respawn** (a new agent with the usual short spawn prompt: "You are E4. Read ..., then E4.md, and do its next step") when any of these hold:
  - the agent has used a lot of tokens, or has compacted;
  - the next step starts a new phase (for example from implementing to landing, or from a fix to its verification);
  - it has been idle long enough that resuming would replay a large, stale transcript;
  - the next step needs a different tier.
- **Before you respawn,** check that the brief's Current state and Next step cover what the agent knew: staged changes, decisions, detached runs. If they don't, resume the old agent once and ask it only to bring the brief up to date. A fresh agent that can't carry on from the brief is a gap in the brief: fix the brief.
- **A resume counts toward the caps,** like a spawn. Resuming also replays the whole transcript, so it isn't free either.

## 5. Review adversarially

- **At each milestone's end,** and after risky changes, spawn `review`. Give it the scope (the diff, epic or area), the project's worst failure class, the spec sections, and where earlier review records live.
- **It cannot edit.** It reports ranked findings, with evidence and proposed fixes, separating confirmed from plausible. You write the review record (for example `Reviews/M3-Review.md`), numbering findings as earlier records do.
- **Send fixes to the owning epics** as normal tasks. Re-review the fixes for anything in the worst failure class.

## 6. Keep the spec doc in sync

If the project mirrors its spec in a separate document (for example a shared doc for the owner), workers quote each changed spec paragraph in full in their reports. Apply those quotes to the mirror in the same session, section by section. Don't paraphrase them. When a report lacks the quotes, ask for them.

## 7. Survive usage limits and restarts

- **Usage limits.** Every agent stops at a usage limit. The `autoContinueAtUsageLimit` setting may not wake the session.
  - **Auto-resume is opt-in.** It is the project owner's choice, and it is off unless they turned it on. They turn it on by telling you, or with a line in the project's CLAUDE.md such as "Auto-resume: allowed". Without that, keep no watchdog: when agents stop at a limit, tell the user, and resume only when they ask.
  - **If the project's owner has allowed auto-resume,** keep a watchdog while agents are working: CronCreate, hourly, session-only, with a prompt like the one below. Crons die with the session and expire after 7 days, so recreate one that is missing.

    > Watchdog: check each agent spawned this session. For each that stopped at a usage limit or a restart, resume it with SendMessage, or respawn it from its brief (section 4): tell it to re-check any half-written files, rerun what it was waiting on, and carry on with its next step. Then run `uak runs list` and `uak lock status`, and resume any lead task that was interrupted.
- **After a Claude Code restart:**
  - Agents do not restart themselves. They come back as stopped. Resume each one with SendMessage, or respawn it from its brief (section 4), and tell it to rerun what it was waiting on.
  - Background shells are dead, and they also die after two hours. Anything that may run over an hour must go through `uak runs start`, which survives both; `uak runs list` shows them, and `uak runs wait` waits for one. A run started some other way can be recorded with `uak runs adopt`.
  - The watchdog, if auto-resume is allowed, is gone. Recreate it.
- **Auto-compaction.** Long lead sessions compact. Keep decisions and state in files (plans, briefs, review records, the project's CLAUDE.md or memory), not only in the conversation. See `${CLAUDE_PLUGIN_ROOT}/docs/SETTINGS.md` for the threshold.

## 8. Optional: pass, then commit, then continue

With the user's agreement, run milestones without checking in:
1. When the milestone's final verification and review pass, commit its changes (never push unless asked).
2. Go straight on to the next milestone's plan and briefs.
3. On a failure, fix it (or send it to the owning epic), re-run, and follow the same loop.
4. Interrupt the user only for a critical question. Use a push notification if one is set up, rather than waiting silently. Record every other decision in the spec, plan or review docs.

Without that agreement, stop at each milestone boundary and report.
