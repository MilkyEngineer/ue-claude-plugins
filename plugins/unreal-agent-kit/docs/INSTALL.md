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

This adds the agents (`runner`, `low`, `medium`, `high`, `xhigh`, `research`, `review`, `architect`; Claude Code shows them as `unreal-agent-kit:<name>`) and the `unreal-agent-workflow` skill. Restart Claude Code, or run `/agents`, to see them.

## 2. Get the source for `uak`

The plugin you just installed holds the source. Claude Code keeps that copy under `~/.claude/plugins/` and replaces it on every update, which is fine: the publish (step 4) installs `uak` outside it. When you start Claude Code in an Unreal project, the plugin's SessionStart hook notices while this version's `uak` isn't published yet. Claude then asks, with your first message, whether to publish it now, with the exact command for the project's engine, and to remove older versions afterwards. Say yes, or run the publish yourself.

The hook is a POSIX `sh` script, so it needs `sh`. Linux and Mac have it. On Windows it comes with Git for Windows (Git Bash), which Claude Code on Windows uses to run hooks. Without `sh` the hook can't run, and nothing reminds you to publish.

You can publish from a clone you control instead:

```
git clone https://github.com/MilkyEngineer/ue-claude-plugins.git
```

Below, `<kit>` means the plugin's folder: the installed copy, or `ue-claude-plugins/plugins/unreal-agent-kit` in a clone. After a plugin update (or pulling a newer version), publish again: the hook reminds you.

## 3. Find the engine's dotnet

The engine ships a .NET SDK at `<Engine root>/Engine/Binaries/ThirdParty/DotNet/<version>/<platform>/`. The version folder is `10.0` in UE 5.8 and `8.0.412` in UE 5.7 (a source build has its own, for example `8.0.300` in 5.6). The platform folder is:

| Host | Folder | Program |
| --- | --- | --- |
| Windows | `win-x64` (`win-arm64` on Arm) | `dotnet.exe` |
| Linux | `linux-x64` (`linux-arm64` on Arm) | `dotnet` |
| Mac | `mac-arm64` (`mac-x64` on Intel) | `dotnet` |

Below, `<dotnet>` means that program. Running the engine's own `dotnet` is enough for the build to find the engine: `uak` links the engine's prebuilt `EpicGames.*` libraries and the third-party libraries beside them, and targets the engine's own .NET version, all taken from the engine whose `dotnet` runs it. To link another engine, set `UAK_ENGINE` to its root, or pass `-p:UakEngineDir=<engine root>`.

**`UAK_ENGINE` also chooses the engine you publish against.** The build reads `UAK_ENGINE` before it looks at the `dotnet` that runs it. So when `UAK_ENGINE` is set, in your environment or in a project's `.claude/settings.local.json` (Claude Code passes that to every command it runs in the project), the next publish links that engine's libraries and targets its .NET version, even when you run another engine's `dotnet` (which fails if that SDK is older than the engine's .NET). Unset it, or pass `-p:UakEngineDir=`, which beats it, to publish against another engine. `uak env` shows the engine `uak` was built against.

**Set `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` before the first use of the bundled SDK.** Without it, the SDK's first run installs an ASP.NET development HTTPS certificate in your user certificate store. `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1` turn off telemetry and the welcome banner.

## 4. Publish `uak`

```
<dotnet> publish <kit>/tools/src/uak -c Release
```

- **What it makes.** A self-contained `uak` for this machine's platform. It runs with no installed .NET and no `DOTNET_ROOT`.
- **Where it goes.** `$UAK_HOME/<kit version>/`, where `UAK_HOME` defaults to `~/.unreal-agent-kit`. The kit version is the `version` in `<kit>/.claude-plugin/plugin.json`. For kit version 0.3.3 that is `~/.unreal-agent-kit/0.3.3/uak.exe` on Windows, and `~/.unreal-agent-kit/0.3.3/uak` on Linux and Mac. The examples below use 0.3.3: use your kit's version.
  - The folder is outside the plugin, which Claude Code replaces on every update.
  - Each kit version gets its own folder, so publishing a new version never overwrites a `uak` that is running.
  - Republishing the same version over itself can fail while a detached run is going, because the run's wrapper keeps that folder's files open. Wait until `uak runs list` shows nothing running.
- **The first self-contained publish downloads the .NET runtime pack from NuGet,** because the engine's SDK doesn't include one, so it needs network access once. That is the only package `uak` needs. Build output stays in `<kit>/tools/bin` and `<kit>/tools/obj`, and the runtime pack goes to your user NuGet cache. The kit's build writes nothing into the engine itself. The bundled SDK may still update its own metadata folder under the engine's `Binaries/ThirdParty/DotNet` directory on first use, as any dotnet does, if that folder can be written.
- **Overrides.** `-r <runtime id>` publishes for another platform, `-o <folder>` publishes somewhere else, and `-p:SelfContained=false` makes a smaller build that needs nothing from NuGet, but needs `DOTNET_ROOT` set to the engine's `dotnet` folder.
- **Fallback.** If the self-contained publish fails, build the solution (step 6) and run `<dotnet> <kit>/tools/bin/uak/Debug/<tfm>/uak.dll <command...>` (`<tfm>` is `net10.0` for UE 5.8, `net8.0` for UE 5.7) wherever these docs say `uak`.

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

**In Claude Code sessions, the plugin puts `uak` on PATH.** Its `bin/` folder holds `uak` (for Git Bash, Linux and Mac) and `uak.cmd` (for PowerShell and cmd). Each runs the published `uak` of the plugin's own version, so after a plugin update you only publish again. Detached runs start from the session, so they find it too.

- Claude Code puts plugin `bin/` folders after your own `PATH` entries. A `uak` folder you added to `PATH` yourself wins: after each update, point it at the new version's folder, or remove it.
- When this version's `uak` isn't published yet, `uak` says so, points here, and exits 2.

**Outside Claude Code** (a terminal, CI, a scheduled task), add the publish folder to `PATH` in your user environment, or call `uak` by its full path:

- **Windows:** add `%USERPROFILE%\.unreal-agent-kit\0.3.3` to your user `Path` (Settings, System, About, Advanced system settings, Environment Variables), or from PowerShell:

  ```powershell
  $dir = "$env:USERPROFILE\.unreal-agent-kit\0.3.3"
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

  By full path: `& "$env:USERPROFILE\.unreal-agent-kit\0.3.3\uak.exe" env` in PowerShell, or `~/.unreal-agent-kit/0.3.3/uak.exe env` in Git Bash.
- **Linux:** add `export PATH="$HOME/.unreal-agent-kit/0.3.3:$PATH"` to `~/.profile` (or your shell's profile). By full path: `~/.unreal-agent-kit/0.3.3/uak env`.
- **Mac:** add the same line to `~/.zprofile`. By full path: `~/.unreal-agent-kit/0.3.3/uak env`.

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

With an SDK 8 engine (UE 5.7, or a 5.6 source build), which predates `--solution`, the test command is `<dotnet> test UnrealAgentKit.sln`. The build goes to `<kit>/tools/bin/<project>/Debug/<tfm>/` (`net10.0` for UE 5.8, `net8.0` for SDK 8 engines). A detached run started from that build keeps its files open, so the build can't be rebuilt while a run is going.

## 7. Point `uak` at the project and engine

Usually nothing is needed. `uak` finds the project by walking up from the current folder to the nearest `.uproject`. It finds the engine as Unreal does:
- a project whose `EngineAssociation` is set uses the engine it names: a version such as `5.8` or a registered build's GUID (the one `Setup.bat` registers), or a path (any value with `/` or `\`), relative to the project's folder. If that engine can't be found, `uak` stops with an error, as Unreal does: it does not fall back to another engine;
- a project whose `EngineAssociation` is empty uses the engine whose root folder holds it, at any depth (a project inside a source build, next to `Engine/` or below).

`UAK_ENGINE` or `-engine=` overrides both. Set these only when that guess is wrong, for example with several projects in one folder, or a source-built engine that isn't registered:

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
1. Build a class library that references `AgentKit.Core.dll` and implements `AgentKit.Core.IUakCommand`. Target a .NET no newer than `uak`'s own, which is the .NET of the engine `uak` was published with (`net10.0` for UE 5.8, `net8.0` for UE 5.7): `uak` can't load a library built for a newer .NET. `net8.0` loads in both. `uak env` shows `uak`'s .NET and engine on its `uak:` line.
2. Put its DLL, and its dependencies, in `<Project>/.uak/commands/`, or in a folder listed in `UAK_COMMAND_PATHS`. That variable takes absolute folders only: `uak` skips a relative entry with a warning, because it would depend on the current folder.
3. Turn project commands on. `<Project>/.uak/commands/` is off by default: set `UAK_PROJECT_COMMANDS=1` (or `true`), or list that folder in `UAK_COMMAND_PATHS`. While it is off, `uak help` and `uak env` mention the folder and say how to turn it on.

Loading a command runs its code, so `uak` loads these folders lazily: only to run a command the kit does not have itself, or for `uak help -all`, which lists the new commands. The host loads only the DLLs in those folders that reference `AgentKit.Core`. The other DLLs there are loaded when a command needs them. If two commands share a name, the first one found wins and `uak` prints a warning.

**Trust:** a command DLL runs as you, with your rights, every time `uak` loads it. Turn project commands on only for a project whose `.uak/commands/` folder you trust as much as its build scripts. A cloned or downloaded project can ship DLLs there.

## Version control

- **git and p4 come from `PATH` only.** `uak` never runs a `git` or `p4` found in the project or current folder.
- **Perforce detection.** `uak` finds a Perforce workspace through a `P4CONFIG` file at or above the project. Global settings alone (a `P4PORT` or `P4CLIENT` set machine-wide, with no `P4CONFIG` file) are not used, and `uak` does not run `p4` then, so it never contacts a server just because one is set machine-wide. To use those global settings, pass `-vcs=p4` (or set `UAK_VCS=p4`).
- **Timeout.** Each `p4` command may take `UAK_P4_TIMEOUT` seconds (default 15). After that `p4` is stopped, so an unreachable server cannot hang a command. Each `git` command may take `UAK_GIT_TIMEOUT` seconds (default 30), for the same reason.
- **Perforce writes.** `uak vcs edit|add|reopen <file>... [-c=<changelist>|default]`, `uak vcs change new -description=<text>`, `uak vcs change describe -c=<changelist> -description=<text>` and `uak vcs shelve -c=<changelist> [-keep-unopened]` (or `uak vcs shelve <file>... [-description=]`, which moves opened files into a new changelist and shelves it) work on this client's pending changelists only, and check each file afterwards. A shelve replaces the shelf (p4 shelve -r): the shelf becomes exactly the opened files, so shelved files that were reverted or moved out of the changelist are deleted from it, and uak prints every file the shelf loses. `-keep-unopened` (p4 shelve -f, the default before 0.3.3) keeps them instead; use it when the shelf is the only copy of that work. `-replace` and `-drop-unopened` are still accepted, and do nothing. Shelving files that sit in a numbered changelist needs `-force`, and half of a move is refused. They never revert or submit. `reopen` and `edit` refuse wildcards and directories unless `-folders` is given, because a directory also takes every other file opened under it. A shelve may run up to 30 minutes, since it sends file contents. Git workspaces get a clear "Perforce only" error.

## Horde

`uak horde` starts Horde preflights of shelved Perforce changes and reports their results. It uses the engine's prebuilt `EpicGames.Horde` (from UnrealBuildTool's folder) and Horde's own login and token cache, which Horde's other tools share.

1. **The server, once.** `uak horde config -server=https://horde.example.com/` stores it in `$UAK_HOME/config.json` (default `~/.unreal-agent-kit/config.json`), shared by every kit version. Without it, the commands use Horde's own default (`UE_HORDE_URL`, then the registry value Horde's tools write on Windows, or `~/.horde.json`), and when there is none they say to ask for the URL. `uak horde config` alone shows which server is used and where it came from.
2. **Sign-in.** Each horde command first uses Horde's cached token, refreshed silently. When that fails, it prints "Opening the Horde sign-in page in your browser...", opens the page and waits up to 600 s (`-login-timeout=<seconds>`), then exits 4 if nobody signed in. `-no-login` never opens the page (CI, scripts). `uak horde login` signs in ahead of time. `uak -verbose horde ...` shows Horde's own messages, which say why a cached token was refused.
   - If your identity provider rejects the silent refresh (for example Entra ID's AADSTS90009), every new process would sign in again: ask your Horde admin to check the server's OIDC scope. Until then, uak's token cache (below) lets commands share one sign-in until the token expires.
   - **uak's token cache.** On Windows, after a sign-in uak keeps the access token and its expiry (from the token's `exp` claim) in `$UAK_HOME/horde/<server>/token.bin`, encrypted with DPAPI for the current user, with the server's URL as extra entropy. Commands use it while it has more than 5 minutes left. When the server refuses it, it is deleted, and the command signs in again and retries only the refused request. A token from `UE_HORDE_TOKEN` is used as it is, never cached. `uak horde logout` deletes it. Elsewhere nothing is cached. uak never prints or logs the token.
3. **Preflight.** `uak horde preflight -c=<shelved changelist>` finds the Horde stream from the workspace's stream (walking up virtual streams), starts the job with the stream's saved build settings, and prints its URL. `-shelve` shelves first, replacing the shelf as `uak vcs shelve -c=` does (`-keep-unopened` keeps shelved files that aren't opened), and always starts a new preflight; it is refused while an auto-submit preflight of the change runs. `-stream=` and `-template=` choose, and `-wait [-timeout=<seconds>]` waits quietly for the result. `uak horde streams` lists the streams and templates. Without `-shelve`, a preflight that uak started and that is still running with the same stream, template, parameters and auto-submit setting, created after the change was last shelved and on the same shelf (uak records each preflight it starts, with the shelf, in `$UAK_HOME/horde/<server>/preflights/`), is reported ("reused: <job>") instead of started again (`-force` starts another).
4. **Build settings.** Each stream's template and parameter values are saved per user and server in `$UAK_HOME/horde/<server>/<stream>/templates.json`. With none saved, a preflight starts nothing and exits 6, printing the templates and parameters; in Claude Code the lead then asks you and saves your answers. `uak horde templates` lists them; `uak horde config -template=<id> -param:<id>=<value>` saves (checked against the server first), `uak horde config -show` shows, and `uak horde config -reset-build` forgets. On a preflight, `-template=` and `-param:<id>=` change one run, and `-use-template-defaults` (CI) uses the template's defaults.
5. **Auto-submit is opt-in.** `-autosubmit` tells Horde to edit the change's description and submit it when the preflight succeeds. It is off unless you pass it. Horde submits the change's current shelf, so uak refuses (exit 5) to start a second auto-submit preflight of a change, to shelve a change again while one runs (`-shelve`, and `uak vcs shelve -c=` when a Horde server is configured), and, with `-shelve -keep-unopened -autosubmit`, to auto-submit shelved files that aren't opened in the change (`-allow-shelved-only` accepts them). Plain `-shelve` deletes such files from the shelf, so they are neither built nor submitted.

`uak horde log -job=<job> [-step=<id or name>] [-errors | -warnings] [-max=<n>] [-context=<n>] [-save]` prints a job's errors and warnings from Horde's log events, de-duplicated and capped, with line numbers and the lines before each error; `-save` also keeps each step's whole log in the state folder. `-issues` on `uak horde job` and `uak horde preflight -wait` adds the same report to the summary.

`uak horde job -id=<job> [-wait -timeout=<seconds>]` reports a job: the result, its URL, and only the failing steps. Exit codes: 0 success, 1 failure or not completed, 2 usage error, 3 still running, 4 not signed in, 5 another error (including 403, not allowed, and uak's refusals above), 6 build settings needed (preflight), 7 success with warnings. `uak horde preflight` prints "Job URL: <server>/job/<id>" as soon as the job exists, before any wait (on standard error under `-json`).

`uak horde config -open=never|created|finished|failed` makes `-wait` open the job's page in your default browser: when it is created, when it ends, or (failed) the first failed step when it fails. The default is never, and `-no-open` skips it for one run.

## Scripts as commands

`uak lock run` and `uak runs start` run any command. A command ending in `.ps1` runs through PowerShell (`powershell.exe` on Windows, `pwsh` elsewhere) with `-NoProfile -ExecutionPolicy Bypass -File <script>`. That bypasses the execution policy for that one script, so only pass scripts you trust. A `.bat` or `.cmd` runs through `cmd.exe`.

## 8. Set up the project

- Add the recommended settings: [SETTINGS.md](SETTINGS.md).
- Paste [CLAUDE-snippet.md](CLAUDE-snippet.md) into the project's CLAUDE.md and fill it in.
