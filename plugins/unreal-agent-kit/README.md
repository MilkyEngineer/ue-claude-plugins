# unreal-agent-kit

A multi-agent workflow for Unreal Engine projects, and `uak`, the CLI that workflow runs on.

One lead agent plans a milestone, splits it into epics and delegates each to a worker at the right effort. Workers share one editor and one build, so every build, test and editor run goes through a queued lock. Long runs are detached, so they survive restarts. A read-only reviewer checks the result before it ships.

## What's in it

- **Agents** (`agents/`):
  - `ue-low` (Sonnet), `ue-medium`, `ue-high` and `ue-xhigh`: workers at rising effort. None can spawn agents.
  - `ue-review`: a read-only adversarial reviewer. It cannot edit files, so it reports findings instead of hiding them in fixes.
  - They share ground rules: take the lock for editor and build runs, detach long runs, ask the lead rather than spawn, keep epic briefs current, never kill processes or commit unless told, and surface permission denials.
- **The `unreal-agent-workflow` skill** (`skills/`): how the lead runs it all. It covers effort tiers and caps, milestone plans with epic briefs (templates included), delegation, adversarial reviews, keeping a spec doc in sync, recovery after restarts, an opt-in usage-limit watchdog, and an optional "pass, then commit, then continue" loop.
- **`uak`** (`tools/`): one CLI, built from C# with the engine's own .NET SDK and `EpicGames.*` libraries.
  - `uak lock run|status`: a fair, queued editor lock that is released when its holder dies.
  - `uak runs start|list|adopt`: detached, tracked runs that outlive shells and restarts.
  - `uak compile`: single-file compile checks through UBT `-SingleFile`, without the lock.
  - `uak build` and `uak test`: builds and automation tests that take the lock themselves, with pass, fail and count checks.
  - `uak vcs status|changed|revision`: version control (Git, Perforce or none).
  - `uak env`: the project, engine, state folder and version control the kit resolved, and how.
  - `uak help [command]`: every command's options. A project can add its own commands in `<Project>/.uak/commands/`, loaded only when you turn them on with `UAK_PROJECT_COMMANDS=1` (they run with your rights, so only for a project you trust).
- **Docs** (`docs/`): [INSTALL.md](docs/INSTALL.md), [SETTINGS.md](docs/SETTINGS.md) and a [CLAUDE.md snippet](docs/CLAUDE-snippet.md).

## Install

```
/plugin marketplace add MilkyEngineer/ue-claude-plugins
/plugin install unreal-agent-kit@ue-claude-plugins
```

Then publish `uak` once with the engine's dotnet (`<engine dotnet> publish tools/src/uak -c Release`, which installs it in `~/.unreal-agent-kit/<version>/`), put it on `PATH`, add the recommended settings, and paste the CLAUDE.md snippet into your project. [INSTALL.md](docs/INSTALL.md) has the steps.

**Requirements:** Claude Code and an Unreal Engine install (UE 5.8 or 5.7). Nothing else: `uak` builds with the .NET SDK bundled in `Engine/Binaries/ThirdParty/DotNet` against the libraries the engine ships, and the published `uak` is self-contained.

## Platform status

- **Windows** is tested, with UE 5.8 and UE 5.7.
- **Linux and Mac:** the code paths exist (engine lookup, process detaching, platform paths), but they are untested.

Main TODOs:
- Test on Linux and Mac: named mutexes between processes for the lock, `setsid` detaching for runs, the `EngineAssociation` lookup in UE's settings folder, and dead-ticket cleanup after a crash.
- Version-control writes (commit, submit, checkout) are out of scope for now. On Perforce, `uak vcs changed` lists opened files only, so it misses files edited offline.

## Using it

Ask the lead to plan a milestone with the workflow skill, or name it: "Use unreal-agent-workflow to plan M1 as epics". Spawn prompts stay short, because the rules live in the agent definitions and the task lives in the brief:

> You are E2. Read `Plans/M1/README.md`, then `Plans/M1/E2.md`, and do its next step.

Workers run builds and editor runs through `uak`. `uak build` and `uak test -filter=<prefix> -name=<unique>` take the editor lock themselves. Any other editor run goes through `uak lock run -name=<unique> -- <command...>`, and a long run through `uak runs start -name=<unique> -owner=<E#> -- <command...>`.

## Design

[DESIGN.md](DESIGN.md) is the contract for the tools and the plugin content.
