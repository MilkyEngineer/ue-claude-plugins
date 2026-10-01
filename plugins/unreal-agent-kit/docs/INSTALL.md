# Installing UnrealAgentKit

UnrealAgentKit has two parts: the Claude Code plugin (agents and the workflow skill), and `uak`, a CLI the agents run. The plugin installs through the marketplace. `uak` is C# source that you publish once, with the .NET SDK that ships inside the engine. Nothing else needs installing.

**Platforms:** Windows is tested. The Linux and Mac code paths exist but are untested (see the README's [Platform status](../README.md#platform-status)).

## 1. Install the plugin

In Claude Code:

```
/plugin marketplace add MilkyEngineer/ue-claude-plugins
/plugin install unreal-agent-kit@ue-claude-plugins
```

Or from a shell: `claude plugin marketplace add MilkyEngineer/ue-claude-plugins`, then `claude plugin install unreal-agent-kit@ue-claude-plugins`.

This adds the agents (`ue-low`, `ue-medium`, `ue-high`, `ue-xhigh`, `ue-review`) and the `unreal-agent-workflow` skill. Restart Claude Code, or run `/agents`, to see them.

## 2. Get the source for `uak`

Claude Code keeps its own copy of the plugin under `~/.claude/plugins/`, and replaces it on every update. Publish `uak` from a clone you control instead:

```
git clone https://github.com/MilkyEngineer/ue-claude-plugins.git
```

Below, `<kit>` means `ue-claude-plugins/plugins/unreal-agent-kit` in that clone. After pulling a newer version, publish again.

## 3. Find the engine's dotnet

The engine ships a .NET SDK at `<Engine root>/Engine/Binaries/ThirdParty/DotNet/<version>/<platform>/`. For UE 5.8 the version folder is `10.0`. The platform folder is:

| Host | Folder | Program |
| --- | --- | --- |
| Windows | `win-x64` (`win-arm64` on Arm) | `dotnet.exe` |
| Linux | `linux-x64` (`linux-arm64` on Arm) | `dotnet` |
| Mac | `mac-arm64` (`mac-x64` on Intel) | `dotnet` |

Below, `<dotnet>` means that program. Running the engine's own `dotnet` is enough for the build to find the engine: `uak` links the engine's prebuilt `EpicGames.*` libraries, and the build takes them from the engine whose `dotnet` runs it. To link another engine, set `UAK_ENGINE` to its root, or pass `-p:UakEngineDir=<engine root>`.

**Set `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` before the first use of the bundled SDK.** Without it, the SDK's first run installs an ASP.NET development HTTPS certificate in your user certificate store. `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1` turn off telemetry and the welcome banner.

## 4. Publish `uak`

```
<dotnet> publish <kit>/tools/src/uak -c Release
```

- **What it makes.** A self-contained `uak` for this machine's platform. It runs with no installed .NET and no `DOTNET_ROOT`.
- **Where it goes.** `$UAK_HOME/<kit version>/`, where `UAK_HOME` defaults to `~/.unreal-agent-kit`. For kit version 0.1.0 that is `~/.unreal-agent-kit/0.1.0/uak.exe` on Windows, and `~/.unreal-agent-kit/0.1.0/uak` on Linux and Mac.
  - The folder is outside the plugin, which Claude Code replaces on every update.
  - Each kit version gets its own folder, so publishing a new version never overwrites a `uak` that is running.
  - Republishing the same version over itself can fail while a detached run is going, because the run's wrapper keeps that folder's files open. Wait until `uak runs list` shows nothing running.
- **The first publish restores NuGet packages,** so it needs network access once. Build output stays in `<kit>/tools/bin` and `<kit>/tools/obj`, and NuGet packages go to your user NuGet cache. The kit's build writes nothing into the engine itself. The bundled SDK may still update its own metadata folder under the engine's `Binaries/ThirdParty/DotNet` directory on first use, as any dotnet does, if that folder can be written.
- **Overrides.** `-r <runtime id>` publishes for another platform, `-o <folder>` publishes somewhere else, and `-p:SelfContained=false` makes a smaller build that needs `DOTNET_ROOT` set to the engine's `dotnet` folder.
- **Fallback.** If the self-contained publish fails, build the solution (step 6) and run `<dotnet> <kit>/tools/bin/uak/Debug/net10.0/uak.dll <command...>` wherever these docs say `uak`.

**Windows (PowerShell):**

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = "false"
$engine = "C:\Program Files\Epic Games\UE_5.8"   # your engine root
& "$engine\Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe" publish "<kit>\tools\src\uak" -c Release
```

**Linux and Mac (sh):**

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
engine="$HOME/UnrealEngine"   # your engine root
"$engine/Engine/Binaries/ThirdParty/DotNet/10.0/linux-x64/dotnet" publish "<kit>/tools/src/uak" -c Release   # mac-arm64 or mac-x64 on a Mac
```

## 5. Put `uak` on PATH, or call it by full path

Every Claude Code session and every detached run must find the same `uak`. Either add the publish folder to `PATH` in your user environment, or write its full path in the project's CLAUDE.md.

- **Windows:** add `%USERPROFILE%\.unreal-agent-kit\0.1.0` to your user `Path` (Settings, System, About, Advanced system settings, Environment Variables), or from PowerShell:

  ```powershell
  $dir = "$env:USERPROFILE\.unreal-agent-kit\0.1.0"
  $key = Get-Item "HKCU:\Environment"
  # Read the raw value, so entries such as %USERPROFILE%\bin stay unexpanded.
  $path = $key.GetValue("Path", "", [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
  if (($path -split ";") -notcontains $dir) {
      $new = (@($path -split ";" | Where-Object { $_ }) + $dir) -join ";"
      Set-ItemProperty "HKCU:\Environment" -Name Path -Value $new -Type ExpandString
      # Tell Windows the environment changed, so new terminals see it (removing an unset variable only broadcasts).
      [Environment]::SetEnvironmentVariable("UAK_UNUSED", $null, "User")
  }
  ```

  This writes the user `Path` only, keeps its existing entries as they were (including unexpanded ones like `%USERPROFILE%\bin`), and does nothing if the folder is already there. Programs started before the change keep the old `Path`: open a new terminal, and restart Claude Code from it, to pick it up.

  By full path: `& "$env:USERPROFILE\.unreal-agent-kit\0.1.0\uak.exe" env` in PowerShell, or `~/.unreal-agent-kit/0.1.0/uak.exe env` in Git Bash.
- **Linux:** add `export PATH="$HOME/.unreal-agent-kit/0.1.0:$PATH"` to `~/.profile` (or your shell's profile). By full path: `~/.unreal-agent-kit/0.1.0/uak env`.
- **Mac:** add the same line to `~/.zprofile`. By full path: `~/.unreal-agent-kit/0.1.0/uak env`.

Restart Claude Code afterwards, so it sees the new `PATH`. When you publish a new kit version, point `PATH` (or CLAUDE.md) at the new version's folder. Then check:

```
uak env
```

It prints the project, the engine, the state folder, the bundled dotnet, UBT, the editor and the version control it found, and how it found each one.

## 6. Optional: build and test the kit

Run from `<kit>/tools`, with the same `<dotnet>` and environment variables. Run them from that folder, not from `<kit>` with a `tools/...` path: `tools/global.json`, which selects the test runner, is only found from there.

```
<dotnet> build UnrealAgentKit.sln
<dotnet> test --solution UnrealAgentKit.sln
```

The build goes to `<kit>/tools/bin/<project>/Debug/net10.0/`. A detached run started from that build keeps its files open, so the build can't be rebuilt while a run is going.

## 7. Point `uak` at the project and engine

Usually nothing is needed. `uak` finds the project by walking up from the current folder to the nearest `.uproject`. It finds the engine that contains the project, else the one the project's `EngineAssociation` names. Set these only when that guess is wrong, for example with several projects in one folder or a source-built engine elsewhere:

| Variable | Meaning |
| --- | --- |
| `UAK_PROJECT` | the `.uproject` file, or a folder holding exactly one |
| `UAK_ENGINE` | the engine root (or its `Engine` folder) |
| `UAK_VCS` | forces `git`, `perforce` (or `p4`) or `none` |
| `UAK_MAX_PARALLEL_ACTIONS` | caps UBT's parallel actions for `uak build` and `uak compile`, for machines short of RAM |
| `UAK_HOME` | the kit's own folder, holding the versioned installs and the fallback `State/` folder (default `~/.unreal-agent-kit`) |
| `UAK_PROJECT_COMMANDS` | `1` (or `true`) loads project commands from `<Project>/.uak/commands/` (see below); off by default |
| `UAK_COMMAND_PATHS` | extra folders of project commands (see below), separated by `;` on Windows and `:` elsewhere; absolute folders only |
| `UAK_P4_TIMEOUT` | seconds each `p4` command may take before it is stopped (default 15) |
| `UAK_GIT_TIMEOUT` | seconds each `git` command may take before it is stopped (default 30) |
| `UAK_LOCK_PRIORITY` | the default lock priority, `High` or `Normal`; `uak runs start -priority=` sets it for a run |
| `UAK_LOCK_NAME` | set by `uak runs start` to the run's name, so the run's lock requests show as `<run>/<name>` |

The global options `-project=`, `-engine=` and `-vcs=` beat the variables. They go before the command, for example `uak -project=MyGame.uproject env`. `-verbose` prints more detail. `uak help <command>`, or `uak <command> -help`, shows a command's options.

Per-project values fit best in the project's `.claude/settings.local.json` under `"env"` (see [SETTINGS.md](SETTINGS.md)).

## Where `uak` keeps its state

The lock queue, run records, logs and test reports go in the first of these folders that can be written:
1. `<Project>/Saved/AgentKit`;
2. `<Engine root>/Engine/Saved/AgentKit/Projects/<project key>`, or `<Engine root>/Engine/Saved/AgentKit` when there is no project;
3. `$UAK_HOME/State/<project key>`, or `$UAK_HOME/State/<engine key>` when there is no project.

The keys come from the project folder and the engine root, so two projects never share a fallback folder, and neither do two engines.

`uak env` shows which one it chose. The folder must be on a local disk, because the lock works per machine.

## Project commands

A project can add its own `uak` commands without changing the kit:
1. Build a class library that references `AgentKit.Core.dll` and implements `AgentKit.Core.IUakCommand`.
2. Put its DLL, and its dependencies, in `<Project>/.uak/commands/`, or in a folder listed in `UAK_COMMAND_PATHS`. That variable takes absolute folders only: `uak` skips a relative entry with a warning, because it would depend on the current folder.
3. Turn project commands on. `<Project>/.uak/commands/` is off by default: set `UAK_PROJECT_COMMANDS=1` (or `true`), or list that folder in `UAK_COMMAND_PATHS`. While it is off, `uak help` and `uak env` mention the folder and say how to turn it on.

Loading a command runs its code, so `uak` loads these folders lazily: only to run a command the kit does not have itself, or for `uak help -all`, which lists the new commands. The host loads only the DLLs in those folders that reference `AgentKit.Core`. The other DLLs there are loaded when a command needs them. If two commands share a name, the first one found wins and `uak` prints a warning.

**Trust:** a command DLL runs as you, with your rights, every time `uak` loads it. Turn project commands on only for a project whose `.uak/commands/` folder you trust as much as its build scripts. A cloned or downloaded project can ship DLLs there.

## Version control

- **git and p4 come from `PATH` only.** `uak` never runs a `git` or `p4` found in the project or current folder.
- **Perforce detection.** `uak` finds a Perforce workspace through a `P4CONFIG` file at or above the project. Global settings alone (a `P4PORT` or `P4CLIENT` set machine-wide, with no `P4CONFIG` file) are not used, and `uak` does not run `p4` then, so it never contacts a server just because one is set machine-wide. To use those global settings, pass `-vcs=p4` (or set `UAK_VCS=p4`).
- **Timeout.** Each `p4` command may take `UAK_P4_TIMEOUT` seconds (default 15). After that `p4` is stopped, so an unreachable server cannot hang a command. Each `git` command may take `UAK_GIT_TIMEOUT` seconds (default 30), for the same reason.

## Scripts as commands

`uak lock run` and `uak runs start` run any command. A command ending in `.ps1` runs through PowerShell (`powershell.exe` on Windows, `pwsh` elsewhere) with `-NoProfile -ExecutionPolicy Bypass -File <script>`. That bypasses the execution policy for that one script, so only pass scripts you trust. A `.bat` or `.cmd` runs through `cmd.exe`.

## 8. Set up the project

- Add the recommended settings: [SETTINGS.md](SETTINGS.md).
- Paste [CLAUDE-snippet.md](CLAUDE-snippet.md) into the project's CLAUDE.md and fill it in.
