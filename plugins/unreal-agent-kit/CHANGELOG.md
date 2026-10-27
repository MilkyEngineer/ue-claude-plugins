# Changelog

## 0.3.5

### Added

- **`uak test -cooked`** runs automation tests in a cooked game: under the editor lock, UAT's BuildCookRun (the engine's `RunUAT` script) builds the game, cooks, stages and paks the project, and runs the game with `-nullrhi -RunAutomationTest=<filter>` until the test queue empties. `-skipbuild` reuses the game's binaries and `-skipcook` the staged build; `-map=` names the maps to cook (the first is opened), `-config=` the configuration (Debug, DebugGame, Development or Test), `-timeout=` the run's limit (default 1800 s). Game arguments after `--` go through `-addcmdline` (`-ExecCmds`, `-testexit`, `-abslog`, `-ReportExportPath`, quotes and `% ! & | < > ^` are rejected). The game's log is `<Project>/Saved/Logs/<name>/Client.log` and UAT's output `<Project>/Saved/Logs/<name>.log`, both printed first. A cooked run passes only when UAT exited 0, `Client.log` ends its queue with `EXIT CODE: 0`, no test failed, every found test completed, at least one passed, and the run didn't time out; the game's own exit code is printed, not judged.
- **`uak test -expect=<file>`** (editor and cooked runs) lists the tests a run must pass, one full test name per line (blank lines and `#` comments ignored). A run expects the listed names its `-filter` matches, matched as the engine matches filters (`StartsWith:`, `^`, `$`, `+`; `Group:` is rejected), and passes only when each of them passed and no other test completed. Missing and unexpected tests are printed one per line.

### Fixed

- **A found test that never completed is listed and counted as skipped.** The engine skips a test that needs more participants than the run has without a `Test Completed` line, and still ends the queue with exit code 0. `uak test` failed such a run (found 1, completed 0) but reported 0 skipped. It now lists the test as `NotRun <name>` (from the found tests the engine logs after `Found N automation tests`) and counts it as skipped, so a run whose only test never ran is NOTHING RAN. The result file has the names in `NotRun`.

### Changed

- `uak test`'s result file has `Mode` (`editor` or `cooked`), `NotRun` and `Expect`.
- The worker and runner agents' definitions show `-cooked` and `-expect=<file>` in their `uak test` usage, with the `MSYS_NO_PATHCONV=1` note for `-map=/Game/...` from Git Bash.
- Publish `uak` again for 0.3.5 (the SessionStart hook offers to).

## 0.3.4

### Changed

- **Four agents run on Opus.** `xhigh`, `research`, `review` and `architect` now set `model: opus` instead of inheriting the lead's model. They produce what the others act on without re-checking (formats and contracts, engine facts, findings, plans), so a lead on a cheaper model no longer lowers them. `medium` and `high` still inherit; `low` and `runner` stay on Sonnet.
- **`architect` at extra-high effort and without a shell.** Its effort is `xhigh` (was `high`), and `Bash` and `PowerShell` are in its `disallowedTools`, so "runs nothing" is enforced rather than asked. It reads with Read, Grep and Glob, and asks the lead for what a command would tell it.
- **`research` at extra-high effort for key facts.** It stays at `high` by default. The workflow skill now tells the lead to spawn it with `effort: xhigh` when the answer pins down an engine fact the design depends on.
- **Read-only engine investigations go to `research`.** `xhigh` no longer lists "deep engine investigations"; it takes an investigation only when it needs code written or changed (instrumentation, experiments).
- **Compile experiments by `research` use existing files only.** The workflow skill no longer asks the lead to name a module for a temporary file, which contradicted the research agent's own rules; an experiment that needs new code goes to a worker.
- **The lead's effort.** The workflow skill and README now recommend running the lead at high effort, and at extra-high for sessions where it does careful work itself (spec edits, review-fix passes).
- `uak` itself is unchanged, but its publish folder follows the kit's version: publish it again for 0.3.4 (the SessionStart hook offers to).

## 0.3.3

### Changed

- **Shelving replaces the shelf by default.** `uak vcs shelve -c=<N>` and `uak horde preflight -shelve` now run `p4 shelve -r`, so the shelf becomes exactly the files opened in the changelist. Before, they ran `p4 shelve -f`, which left files that were reverted or moved out of the changelist on the shelf: a preflight built them, and a Horde auto-submit would have submitted them.
  - Every file the shelf loses is printed ("REMOVED from the shelf"; `removed` in the JSON, now also in `uak horde preflight -json`).
  - Shelving a changelist with nothing opened is still refused: `-r` would empty the shelf.
  - A file that leaves the changelist while uak shelves still fails the command, naming it.
- **`-keep-unopened`** on both commands brings back `p4 shelve -f`: shelved files that are no longer opened stay on the shelf, listed as kept. Use it when the shelf is the only copy of that work.
- **`-replace` and `-drop-unopened`** on `uak vcs shelve` are accepted and do nothing, so older scripts keep working.
- **`-allow-shelved-only`** on `uak horde preflight` now needs `-shelve -keep-unopened -autosubmit`. Without `-keep-unopened` the files it would let Horde submit are deleted from the shelf instead, so the old `-shelve -autosubmit -allow-shelved-only` is a usage error rather than a silent change. The refusal to auto-submit shelved files that aren't opened applies only with `-keep-unopened`.

## 0.3.2

### Added

- **`uak horde log -job=<id>`:** a Horde job's errors and warnings, without reading whole logs. It reads the events Horde found in the steps' logs (each with its severity), not the log text, and prints per step one line (outcome, counts, the log's URL), then each distinct event with its line number, how often it occurred, and the lines before it. Errors come first; an event repeated within a step, or already shown under an earlier step, is only counted.
  - Without `-step=`, the failed steps, then those with warnings. `-step=` takes a step id or name (or part of a name).
  - `-errors` or `-warnings` keeps one severity; `-max=` (default 10) caps the distinct events per step; `-context=` sets the lines before each event (default 2 before errors, none before warnings); `-json` for scripts.
  - `-save` also saves each step's whole log as text in the state folder (`Logs/horde/<job>/`) and prints where.
  - Exit codes are those of `uak horde job`: the job's result (0, 1, 3, 7), 4 not signed in, 5 another error (no such job or step).
- **`-issues`** on `uak horde job` and `uak horde preflight -wait`: the same report, with the defaults, after the summary.

## 0.3.1

### Added

- **Detached runs keep the machine awake.** On Windows, `uak runs start` now keeps the system and the display on for the whole run, so a laptop doesn't sleep, or enter Modern Standby, under a long build or a GPU-heavy editor run.
  - `-allow-sleep` turns it off for one run; the record holds it as `AllowSleep`.
  - If Windows refuses the request, the run's output says so.
  - A closed lid still sleeps the machine.
  - Linux and Mac aren't supported yet.

## 0.3.0

### Breaking

- **The agents lost their `ue-` prefix.** Claude Code already shows the plugin's name, so `unreal-agent-kit:ue-review` is now `unreal-agent-kit:review`. Update any CLAUDE.md, plan or brief that names an agent:

  | Before 0.3.0 | From 0.3.0 |
  | --- | --- |
  | `ue-runner` | `runner` |
  | `ue-low` | `low` |
  | `ue-medium` | `medium` |
  | `ue-high` | `high` |
  | `ue-xhigh` | `xhigh` |
  | `ue-research` | `research` |
  | `ue-review` | `review` |

### Added

- **`architect` agent:** drafts a milestone's plan, briefs README and epic briefs from the workflow skill's templates, with a file-ownership map, contracts, suggested tiers, and risks with their tests. It writes only those drafts and runs nothing. Default cap 1.
- **Perforce writes:** `uak vcs shelve`, `uak vcs edit`, `uak vcs add`, `uak vcs reopen`, `uak vcs change new` and `uak vcs change describe`, on this client's pending changelists only. Nothing reverts or submits. `shelve -replace` refuses to delete shelved files that aren't opened unless `-drop-unopened`, prints what the shelf loses, and fails, naming it, when it loses a file nobody asked to drop; every shelve lists shelved files that changed and those kept although no longer opened. `shelve -c=` is refused while an auto-submit Horde preflight of the change runs (when a Horde server is configured; a warning when Horde can't be asked). Half moves are refused; a description change puts back files a concurrent reopen moved in or out, or moves none and says so when it can't tell which.
- **Horde:** `uak horde config`, `uak horde login`, `uak horde logout`, `uak horde streams`, `uak horde templates`, `uak horde preflight` and `uak horde job`, on the engine's prebuilt EpicGames.Horde.
  - The server is set once per user in `~/.unreal-agent-kit/config.json`.
  - Commands sign in silently, else open the Horde sign-in page and wait (`-login-timeout=`, default 600 s; `-no-login` for CI).
  - A preflight prints its job URL at once, and auto-submits only with `-autosubmit`. Without `-shelve` it reports ("reused: <job>") a still-running preflight that uak started, with the same stream, template, parameters and auto-submit setting, created after the change was last shelved and on the same shelf (uak records each preflight it starts), instead of starting another; with `-shelve` it always starts a new one. Horde submits the change's current shelf, so uak won't shelve under a running auto-submit preflight, won't start a second auto-submit preflight of a change, and with `-shelve -autosubmit` won't auto-submit shelved files that aren't opened (`-allow-shelved-only`); these refusals exit 5.
  - `-wait` waits quietly and ends with a short summary and exit codes for agents (0 success, 1 failure, 2 usage error, 3 still running, 4 not signed in, 5 error, 6 build settings needed, 7 warnings).
  - `uak horde config -open=` can open the job's page in the browser.
  - Build settings per server and stream (`~/.unreal-agent-kit/horde/<server>/<stream>/templates.json`): a template and its parameters, saved with `uak horde config -template= -param:<id>=`. A preflight with none saved starts nothing and exits 6 with the templates and parameters, so the lead asks the user. `-template=`/`-param:` change one run; `-use-template-defaults` for CI.
  - On Windows, the access token is cached per server (`~/.unreal-agent-kit/horde/<server>/token.bin`, DPAPI for the current user) until 5 minutes before it expires; a refused token is deleted, and the command signs in again and retries only the refused request, once. A wait that rebuilds its connection keeps Horde's own self-refreshing sign-in, and a sign-in mid-wait ends at the wait's `-timeout`. A token from `UE_HORDE_TOKEN` is never cached. `uak horde logout` deletes it.
