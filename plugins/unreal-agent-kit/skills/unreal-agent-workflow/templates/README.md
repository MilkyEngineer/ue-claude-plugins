# <Milestone> workstream briefs

Each <Milestone> workstream has one brief here: `W1.md` to `W<n>.md`. A brief holds everything an agent needs to start or resume that workstream cold. It lists:
- what the workstream owns and must not touch;
- the contracts it implements or depends on;
- the spec sections it follows;
- its done criteria;
- its current state and next step.

[<Milestone>-Plan.md](../<Milestone>-Plan.md) is the plan, and it wins over a brief where the two disagree. The briefs are how the plan is carried out, day to day.

## How agents use a brief

- **Starting or resuming.** The spawn prompt names the workstream, for example "You are W2: read `<briefs folder>/W2.md` and do its next step". Read this README first, then your brief, then the plan and spec sections the brief links.
- **Handing back.** This applies whenever you stop, finish, or are asked for a report.
  - First rewrite your brief's **Current state** and **Next step**, so that an agent with no other context could carry on from them.
  - Include:
    - the state of every piece of work (verified / applied, not verified / written, not applied);
    - the exact commands for the next runs;
    - any detached runs still going (their `uak runs` names);
    - any cache or format versions you bumped.
  - Keep what is finished short. Detail belongs in the code, the tests and the report.
- **Ownership of the brief.** Only the workstream's agent edits its own "Current state", "Next step" and "Gotchas". The lead edits the rest (scope, ownership, done criteria) and may add notes.

## File ownership map

| Area | Owner |
| --- | --- |
| <modules, files or folders> | W1 |
| <...> | W2 |
| Shared contracts: <headers, formats, settings> | The lead. Change them only by agreement (ask with SendMessage to "main"). |

<Note any file two workstreams share, and who coordinates edits to it.>

## Shared facts

- **Test data:** <maps, worlds, fixtures, and how they are generated>.
- **Verification:** <the end-to-end command, for example `uak test -filter=<Project>.<Area>`, or a verification script run through `uak runs start`>.
- **Versions now:** <each cache, format or data version and its current value>. Whoever bumps a version records it in their brief.
- **Parallelism cap:** <for example `UAK_MAX_PARALLEL_ACTIONS=3` on a 16 GB machine>.

## Tooling

All runs go through `uak`. See `uak help` and the kit's docs.

- `uak build`, `uak test -filter=<prefix> [-gpu] -name=<unique>`: they take the editor lock themselves.
- `uak lock run -name=<unique> -- <command...>`: any other editor, commandlet or game run, one invocation at a time. `uak lock status` shows the holder and the queue.
- `uak runs start -name=<unique> -owner=<W#> -- <command...>`: runs that may outlive a shell (over two hours, or across a restart). `uak runs list [-all]` follows them.
- `uak compile <file>... [-dependents]`: single-file compile checks, without the lock.
- `uak vcs changed`, `uak vcs status <path>...`: what changed, through Git or Perforce.

## Gotchas everyone has hit

- **Never wrap a long run in `timeout`.** It kills runs that are waiting on the lock.
- **Background shells die when Claude Code restarts, and after two hours.** Use `uak runs start`.
- **A crash in one test ends the whole run** and hides every later test. `uak test` then fails, because fewer tests completed than were found: read the editor log (`<Project>/Saved/Logs/<name>.log`).
- **A green run without `-gpu` says nothing about GPU tests.** They skip.
- **After editing a header that another module includes,** compile-check it through those modules (`uak compile <header> -dependents`). A clean build of your own module says nothing about the others.
- **A build can fail in another workstream's file mid-edit.** Wait and retry, as the agent rules say.
- <Add project-specific ones as they are found.>
