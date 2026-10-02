# Changelog

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
