# <Milestone> epic briefs

Each <Milestone> epic has one brief here: `E1.md` to `E<n>.md`. A brief holds everything an agent needs to start or resume that epic cold. It lists:
- what the epic owns and must not touch;
- the contracts it implements or depends on;
- the spec sections it follows;
- its done criteria;
- its current state and next step.

[<Milestone>-Plan.md](../<Milestone>-Plan.md) is the plan, and it wins over a brief where the two disagree. The briefs are how the plan is carried out, day to day.

## How agents use a brief

- **Starting or resuming.** The spawn prompt names the epic, for example "You are E2: read `<briefs folder>/E2.md` and do its next step". Read this README first, then your brief, then the plan and spec sections the brief links.
- **Handing back.** This applies whenever you stop, finish, or are asked for a report.
  - First rewrite your brief's **Current state** and **Next step**, so that an agent with no other context could carry on from them.
  - Include:
    - the state of every piece of work (verified / applied, not verified / written, not applied);
    - the exact commands for the next runs;
    - any detached runs still going (their `uak runs` names);
    - any cache or format versions you bumped.
  - Keep what is finished short. Detail belongs in the code, the tests and the report.
- **Ownership of the brief.** Only the epic's agent edits its own "Current state", "Next step" and "Gotchas". The lead edits the rest (scope, ownership, done criteria) and may add notes.

## File ownership map

| Area | Owner |
| --- | --- |
| <modules, files or folders> | E1 |
| <...> | E2 |
| Shared contracts: <headers, formats, settings> | The lead. Change them only by agreement (ask with SendMessage to "main"). |

<Note any file two epics share, and who coordinates edits to it.>

## Shared facts

- **Test data:** <maps, worlds, fixtures, and how they are generated>.
- **Verification:** <the end-to-end command, for example `uak test -filter=<Project>.<Area>`, or a verification script run through `uak runs start`>.
- **Versions now:** <each cache, format or data version and its current value>. Whoever bumps a version records it in their brief.
- **Parallelism cap:** <for example `UAK_MAX_PARALLEL_ACTIONS=3` on a 16 GB machine>.

## Tooling

All runs go through `uak`. See `uak help` and the kit's docs.

- `uak build`, `uak test -filter=<prefix> [-gpu | -windowed] -name=<unique>`: they take the editor lock themselves. More UBT or editor arguments go after `--`.
- `uak lock run -name=<unique> -- <command...>`: any other editor, commandlet or game run, one invocation at a time. `uak lock status` shows the holder and the queue.
- `uak runs start -name=<unique> -owner=<E#> -- <command...>`: runs that may take over an hour, so they outlive a shell (which dies after two hours, and on a restart). `uak runs list [-all]` lists them.
- `uak runs wait -name=<name> -timeout=<seconds>`: waits for a detached run to end. Run it in a background shell whose own time limit is longer than `-timeout` and within the shell's two-hour limit. Exit 0: the run exited 0. Exit 1: it failed, or ended with no exit code. Exit 3: `-timeout` passed with the run still going; start another wait.
- `uak compile <file>... [-dependents]`: single-file compile checks, without the lock.
- `uak vcs changed`, `uak vcs status <path>...`: what changed, through Git or Perforce.

## Gotchas everyone has hit

- **Never wrap a long run in `timeout`.** It kills runs that are waiting on the lock.
- **Background shells die when Claude Code restarts, and after two hours.** Use `uak runs start` for anything that may run over an hour.
- **Never poll a run by hand.** Wait with one background `uak runs wait`; `uak runs list` hides finished runs and exits 0 either way.
- **A crash in one test ends the whole run** and hides every later test. `uak test` then fails, because fewer tests completed than were found: read the editor log (`<Project>/Saved/Logs/<name>.log`).
- **A green run without `-gpu` says nothing about GPU tests.** They skip. Tests that need a real viewport or Slate windows need `-windowed`.
- **After editing a header that another module includes,** compile-check it through those modules (`uak compile <header> -dependents`). A clean build of your own module says nothing about the others.
- **A build can fail in another epic's file mid-edit.** Wait and retry, as the agent rules say.
- <Add project-specific ones as they are found.>
