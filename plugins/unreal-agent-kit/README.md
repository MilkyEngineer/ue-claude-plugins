# unreal-agent-kit

A multi-agent workflow for Unreal Engine projects, and `uak`, the CLI that workflow runs on.

One lead agent plans a milestone, splits it into epics and delegates each to a worker at the right effort. Workers share one editor and one build, so every build, test and editor run goes through a queued lock. Long runs are detached, so they survive restarts. A read-only reviewer checks the result before it ships.

## What's in it

- **Agents** (`agents/`):
  - `low` (Sonnet), `medium`, `high` and `xhigh`: workers at rising effort. None can spawn agents.
  - `runner` (Sonnet, cannot edit): runs a given list of builds, tests and verifications and reports with evidence, so a verification never changes what it verifies.
  - `research` (read-only): engine and codebase investigations, answered with `file:line` evidence. Its reading never conflicts with the workers' edits; its compile and test experiments queue on the lock like any run.
  - `review`: a read-only adversarial reviewer. It cannot edit files, so it reports findings instead of hiding them in fixes.
  - `architect`: drafts a milestone's plan and epic briefs from the skill's templates (ownership map, contracts, tiers, risks with their tests), citing `file:line` for engine behaviour. It writes only those drafts, never code, and runs nothing.
  - Claude Code shows them as `unreal-agent-kit:<name>`, for example `unreal-agent-kit:review`. Before 0.3.0 they were named `ue-<name>`: see [CHANGELOG.md](CHANGELOG.md).
  - They share ground rules: take the lock for editor and build runs, detach long runs, ask the lead rather than spawn, keep epic briefs current, never kill processes or commit unless told, and surface permission denials.
- **The `unreal-agent-workflow` skill** (`skills/`): how the lead runs it all. It covers effort tiers and caps, milestone plans with epic briefs (templates included), delegation, when to resume an agent and when to respawn it from its brief, adversarial reviews, keeping a spec doc in sync, recovery after restarts, an opt-in usage-limit watchdog, and an optional "pass, then commit, then continue" loop.
- **`uak`** (`tools/`): one CLI, built from C# with the engine's own .NET SDK and `EpicGames.*` libraries.
  - `uak lock run|status`: a fair, queued editor lock that is released when its holder dies.
  - `uak runs start|list|wait|adopt`: detached, tracked runs that outlive shells and restarts, and a wait that ends when a run does.
  - `uak compile`: single-file compile checks through UBT `-SingleFile`, without the lock.
  - `uak build` and `uak test`: builds and automation tests that take the lock themselves, with pass, fail and count checks. Tests run headless, or in a window with `-windowed`. More UBT or editor arguments go after `--`.
  - `uak vcs status|changed|revision`: version control (Git, Perforce or none).
  - `uak vcs edit|add|reopen|shelve` and `uak vcs change new|describe`: Perforce writes on this client's pending changelists, each checked afterwards. They never revert or submit.
  - `uak horde config|login|logout|streams|templates|preflight|job`: Horde preflights of shelved changes, with a quiet `-wait` for agents (exit codes for success, failure, warnings, still running and not signed in). The server is set once per user; a command opens the Horde sign-in page in the browser when it needs one (`-no-login` never does), and on Windows a sign-in is kept, encrypted, until the token expires. Build settings (template and parameters) are saved per stream: the first preflight exits 6 so the lead can ask the user.
  - `uak env`: the project, engine, state folder and version control the kit resolved, and how.
  - `uak help [command]`: every command's options. A project can add its own commands in `<Project>/.uak/commands/`, loaded only when you turn them on with `UAK_PROJECT_COMMANDS=1` (they run with your rights, so only for a project you trust).
- **A SessionStart hook** (`hooks/`): in an Unreal project, it says when this plugin version's `uak` isn't published yet, and has Claude ask, with your first message, whether to publish it now (with the exact command for the project's engine) and remove older versions. It is a POSIX `sh` script: on Windows it needs Git for Windows (Git Bash), which Claude Code on Windows uses to run hooks.
- **`uak` on PATH** (`bin/`): in Claude Code sessions, `uak` runs this plugin version's published `uak`, from Git Bash, PowerShell or cmd.
- **Docs** (`docs/`): [INSTALL.md](docs/INSTALL.md), [SETTINGS.md](docs/SETTINGS.md) and a [CLAUDE.md snippet](docs/CLAUDE-snippet.md).

## Install

```
/plugin marketplace add MilkyEngineer/ue-claude-plugins
/plugin install unreal-agent-kit@ue-claude-plugins
```

Then publish `uak` once with the engine's dotnet: `<engine dotnet> publish tools/src/uak -c Release`, which installs it in `~/.unreal-agent-kit/<version>/`. A SessionStart hook reminds you while it isn't published, and after each plugin update. Put it on `PATH`, add the recommended settings, and paste the CLAUDE.md snippet into your project. [INSTALL.md](docs/INSTALL.md) has the steps.

**Requirements:** Claude Code and Unreal Engine 5.8 or 5.7, installed or built from source, and `sh` for the hook (Git Bash on Windows). Nothing else: `uak` builds with the .NET SDK bundled in `Engine/Binaries/ThirdParty/DotNet` against the libraries the engine ships, and the published `uak` is self-contained.

## Platform status

- **Windows** is tested, with installed UE 5.8 and UE 5.7, and a UE 5.6 source build.
- **Linux and Mac:** the code paths exist (engine lookup, process detaching, platform paths), but they are untested.

Main TODOs:
- Test on Linux and Mac: named mutexes between processes for the lock, `setsid` detaching for runs, the `EngineAssociation` lookup in UE's settings folder, and dead-ticket cleanup after a crash.
- Version-control writes are Perforce only, and stop at shelving: nothing commits, submits or reverts. On Perforce, `uak vcs changed` lists opened files only, so it misses files edited offline.
- Horde: tested with UE 5.7's EpicGames.Horde against a 5.7 server.

## Using it

Ask the lead to plan a milestone with the workflow skill, or name it: "Use unreal-agent-workflow to plan M1 as epics". Spawn prompts stay short, because the rules live in the agent definitions and the task lives in the brief:

> You are E2. Read `Plans/M1/README.md`, then `Plans/M1/E2.md`, and do its next step.

Workers run builds and editor runs through `uak`. `uak build` and `uak test -filter=<prefix> -name=<unique>` take the editor lock themselves. Any other editor run goes through `uak lock run -name=<unique> -- <command...>`, and a run that may take over an hour through `uak runs start -name=<unique> -owner=<E#> -- <command...>`, followed by one background `uak runs wait -name=<unique> -timeout=<seconds>`.

## Design

[DESIGN.md](DESIGN.md) is the contract for the tools and the plugin content.
