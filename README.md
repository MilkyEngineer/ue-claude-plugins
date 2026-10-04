# ue-claude-plugins

Claude Code plugins for Unreal Engine development.

## Install

```
/plugin marketplace add MilkyEngineer/ue-claude-plugins
/plugin install unreal-agent-kit@ue-claude-plugins
```

Or from a shell: `claude plugin marketplace add MilkyEngineer/ue-claude-plugins`, then `claude plugin install <plugin>@ue-claude-plugins`.

## Plugins

### unreal-agent-kit

A multi-agent workflow for Unreal Engine projects, and `uak`, the CLI it runs on. One lead plans a milestone, splits it into epics and delegates each to a worker at the right effort. Workers share one editor, so builds and editor runs queue on a lock.

**Agents spend tokens on work, not on waiting.**

- **Horde preflights in one call.** `uak horde preflight -c=<CL> -shelve -wait` shelves the change, finds its stream and template, starts the job and prints its link at once. Then it waits quietly and exits with a verdict code (success, warnings, failure, still running) and links to the failed steps. An hour-long build costs an agent one tool call and a few lines of output, not an hour of polling and pages of logs.
- **Waiting is cheap.** A preflight `-wait` or `uak horde job -wait` runs in a background shell and wakes the agent only when the job ends. Runs that may outlast a shell or a restart go through `uak runs start`, and `uak runs wait` wakes the agent when they end. No turns are spent checking in.
- **Verdicts, not logs.** Builds and test runs end in one line with counts and a log path, checked against the tests expected, so an agent reads a result instead of a log. When a Horde job fails, `uak horde log` prints only its errors and warnings, de-duplicated, with line numbers.
- **Parallel agents, queued runs.** One editor lock across every agent and session, queued and released if its holder dies. Builds, tests and editor runs take turns instead of fighting over the same binaries, and nobody waits on a stale lock.
- **Effort matched to the task.** Sonnet runners and low-effort workers take routine runs and small fixes. High and extra-high effort is kept for the hard problems, read-only reviewers check each milestone, and an architect drafts the plans.
- **Perforce built in.** Edit, add, reopen, new changelists, descriptions and shelving, with guards against wildcards and other clients' changelists. Submitting stays with people; Horde auto-submit is opt-in, never the default.
- **Set up once.** On a stream's first preflight, uak lists the build templates and their parameters, Claude asks you, and the answers are saved for every later preflight. Horde sign-in opens in the browser only when needed; on Windows, later commands reuse it while it lasts.

**Contents**

- **Agents:** `low` (Sonnet), `medium`, `high` and `xhigh` workers, none of which can spawn agents; `runner`, which runs and reports but cannot edit; two read-only agents, `research` for engine investigations and `review` for adversarial reviews; and `architect`, which drafts milestone plans and briefs. Claude Code shows them as `unreal-agent-kit:<name>`.
- **Workflow skill:** effort tiers and caps, milestone plans with epic briefs (templates included), delegation, resume-or-respawn, reviews, spec-doc sync, and an opt-in usage-limit and restart watchdog.
- **`uak`:** a queued editor lock that is released when its holder dies; detached runs that survive shells and restarts; single-file compile checks through UBT; build and automation-test wrappers with count checks; Git and Perforce status; Perforce edit, add, reopen, changelists and shelving; Horde preflights and job watching.

**Requirements:** an Unreal Engine install (UE 5.8 or 5.7). `uak` is published once, as a self-contained program, with the engine's bundled .NET SDK: see [INSTALL.md](plugins/unreal-agent-kit/docs/INSTALL.md). Windows is tested; the Linux and Mac code is untested.

## License

MIT
