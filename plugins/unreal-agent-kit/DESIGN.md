# UnrealAgentKit: design

UnrealAgentKit is a Claude Code plugin with two parts:
- a multi-agent workflow for Unreal Engine projects: tiered workers, a runner that cannot edit, a read-only researcher and a read-only reviewer, epic briefs, and delegation;
- the tools that workflow needs, gathered in one CLI, `uak`:
  - a queued lock around editor and build runs;
  - detached runs that are tracked, and a wait for one to end;
  - a single-file compile check;
  - build and test wrappers;
  - a version-control abstraction, with Perforce writes up to shelving;
  - Horde preflights and a quiet wait for their results.

It must not depend on any particular engine, project, platform or version-control system. Apart from Claude Code, it uses only what building Unreal Engine already requires: the engine's bundled .NET SDK, and the prebuilt C# libraries the engine ships (`EpicGames.*` and the third-party libraries beside them). `uak` itself needs no NuGet package. NuGet is needed only for the tests (MSTest.Sdk) and for a self-contained publish (the .NET runtime pack, which the bundled SDK lacks).

The owner decided on 2026-10-01:
- the name is UnrealAgentKit, living in the `ue-claude-plugins` marketplace;
- UE 5.8 and 5.7, installed or built from source (a source build has no `Engine/Build/InstalledBuild.txt`; tested with a 5.6 source build, whose projects may sit inside the engine's root folder);
- Windows now, but keep everything platform-neutral (no Windows-only code without a Linux/Mac path or a clear TODO).

## Layout

```
plugins/unreal-agent-kit/
  .claude-plugin/plugin.json
  agents/            tiered workers, the runner, the researcher and the reviewer (generic ground rules)
  skills/            the workflow skill (briefs, delegation, resume or respawn, reviews, doc sync) and templates
  hooks/             SessionStart: says when this version's uak isn't published yet
  bin/               uak and uak.cmd: run this version's published uak, so uak is on PATH in Claude Code sessions
  docs/              install, settings, CLAUDE.md snippet
  tools/
    UnrealAgentKit.sln, Directory.Build.props, global.json, build/EpicGames.props
    src/AgentKit.Core      context resolution, process running, JSON, logging, IUakCommand
    src/AgentKit.Locking   the queued lock
    src/AgentKit.Runs      detached runs and their registry
    src/AgentKit.Vcs       IVersionControl: Git, Perforce, None; Perforce writes (edit, add, reopen, change, shelve)
    src/AgentKit.Horde     uak horde: the Horde server setting, sign-in, streams, preflights and jobs
    src/AgentKit.Unreal    compile (UBT -SingleFile), build, automation tests
    src/uak                the CLI host
    src/EpicGames.Perforce.FromSource  the engine's EpicGames.Perforce source, built when no prebuilt DLL exists
    tests/AgentKit.*.Tests MSTest (MSTest.Sdk), one project per library
```

## Build

`<Engine>/Binaries/ThirdParty/DotNet/<version>/<platform>/dotnet build tools/UnrealAgentKit.sln`, using the engine's bundled SDK.

- **Target framework:** the engine's own, read from the `tfm` in its `UnrealBuildTool.runtimeconfig.json` (`UakEngineTargetFramework`), so the engine's libraries always load. UE 5.8 bundles SDK 10.0.203 (`net10.0`); UE 5.7 bundles 8.0.412 (`net8.0`, C# 12). When that file can't be read, the running SDK's own major version (`UakSdkTargetFramework`, from the `sdk/<version>` folder name in `MSBuildBinPath`), which that SDK can always build; `net10.0` only when neither is known.
- **Newer .NET APIs** stay out of the code, or behind `#if NET9_0_OR_GREATER` with a .NET 8 path (`UakJson`'s tab indentation is the one case). Differences between engine versions' libraries are keyed on what the engine ships, not on its version: `UAK_PERFORCE_TRACER` is defined when OpenTelemetry.Api sits beside EpicGames.Perforce (UE 5.8's `IPerforceConnection` has a `Tracer`; 5.7's has none).
- Output goes under `tools/bin` and `tools/obj`. Never write into an engine directory: installed engines sit under Program Files. (The bundled SDK may update its own metadata folder under the engine's DotNet directory; the kit's build writes nothing else there.)
- Tests: from `tools/`, `dotnet test --solution UnrealAgentKit.sln` with SDK 10 (Microsoft.Testing.Platform, set in `tools/global.json`, which is only found from `tools/`), or `dotnet test UnrealAgentKit.sln` with SDK 8 (VSTest; the test projects support both).
- Set `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1` and `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` for kit builds.
- Warnings are errors.

### Installing `uak` (lead's decision, 2026-10-01)

- **How:** a self-contained publish of `tools/src/uak` for the host's runtime identifier, so it runs with no system-wide dotnet.
- **Where:** `$UAK_HOME` (default `~/.unreal-agent-kit`) plus `/<kit version>/`. That is outside the plugin folder, because Claude Code replaces plugin folders on update, and versioned, so an update never overwrites a running binary.
- **Fallback:** if self-contained doesn't work cleanly, `<engine dotnet> uak.dll`.
- **Which engine:** the publish builds against the engine `build/EpicGames.props` finds, so a `UAK_ENGINE` set in the environment (or in a project's `.claude/settings.local.json`, which Claude Code passes to every command) chooses the engine of the next publish too, even when another engine's `dotnet` runs it. The docs say so.
- **NuGet:** the bundled SDKs carry no .NET runtime pack, so the first self-contained publish downloads one (Microsoft.NETCore.App.Runtime.<rid>). `-p:SelfContained=false` needs nothing from NuGet, but then `uak` runs on the engine's dotnet (`DOTNET_ROOT`).
- **Project commands:** the host also loads `IUakCommand` assemblies from the folders in `UAK_COMMAND_PATHS`, and from `<Project>/.uak/commands/` only when `UAK_PROJECT_COMMANDS=1` (or `true`) or that folder is listed in `UAK_COMMAND_PATHS` (a cloned repository's code never runs by default). Folders outside the kit load lazily: only for a command the kit lacks, or for `uak help -all`. Entries in `UAK_COMMAND_PATHS` must be absolute; a relative entry is skipped with a warning, because it would depend on the current folder. That way a project adds its own tools without changing the kit. A command assembly must target a framework no newer than `uak`'s, which is the engine's that `uak` was published with (`net8.0` loads on both UE 5.7 and 5.8 builds); `uak env`'s `uak:` line shows `uak`'s framework, the runtime it runs on, and the engine version it was built against (`UakEngineVersion`, embedded at build time as assembly metadata by `build/EpicGames.props`).

## Engine libraries

`build/EpicGames.props` is owned by Core.
- **Engine directory.** It finds the engine from the `UakEngineDir` property, else from the `UAK_ENGINE` environment variable, else from the engine whose bundled dotnet runs the build. It resolves `UakEngineRoot` (the root, with a trailing slash) and `UakEpicGamesDir`: `<root>Engine/Binaries/DotNET/UnrealBuildTool/`, or `AutomationTool/` beside it when `UnrealBuildTool/` has no `EpicGames.Build.dll`. It also reads `UakEngineVersion` (Major.Minor.Patch) from the engine's `Build.version`.
- **References.** It references the prebuilt `EpicGames.Core`, `.Build`, `.IoHash` and `.MsBuild` that the engine ships in `.../UnrealBuildTool`, and their run-time dependency closure from the same folder (Microsoft.Extensions.*, Polly, Blake3 and its native library, and the rest, as `UnrealBuildTool.deps.json` lists them). All are file references with `Private=true`, so they are copied next to `uak`. No NuGet package is involved, and the versions always match the engine.
- **EpicGames.Perforce.** The engine ships it prebuilt with AutomationTool: in `AutomationUtils/<tfm>/` in UE 5.8, and directly in `AutomationUtils/` in UE 5.7. AgentKit.Vcs references that DLL itself, based on `$(UakEngineRoot)`: the resolved engine root, with a trailing slash. `UakEngineDir` is only the optional input. It is not in `build/EpicGames.props`, because only AgentKit.Vcs and its tests need it.
  - Its one third-party dependency comes from the engine too: OpenTelemetry.Api beside it (5.8), or System.Linq.Async in `AutomationTool/` (5.7). No NuGet package.
  - We use our own `P4ProcessConnection`, an `IPerforceConnection` that runs `p4 -G` (found on PATH only) with a per-command timeout (`UAK_P4_TIMEOUT`, 15 s by default), so no native library is needed.
  - `src/AgentKit.Vcs/EpicGames.Perforce.props` holds the reference. It prefers `AutomationUtils/$(TargetFramework)/`, then `AutomationUtils/`, and `-p:UakPerforceDll=<path>` overrides both.
  - **Source builds** have no AutomationTool until someone builds it, so there is no prebuilt DLL. Then `src/EpicGames.Perforce.FromSource` compiles the engine's own `Engine/Source/Programs/Shared/EpicGames.Perforce` source (assembly name `EpicGames.Perforce`, versioned with `UakEngineVersion`) into the kit's `tools/bin`, against the engine's prebuilt EpicGames.Core. Nothing is written into the engine. The kit's warning and analyzer rules don't apply to that code.
    - Its third-party dependency (System.Linq.Async up to UE 5.7, OpenTelemetry.Api in 5.8) is looked for, by `src/EpicGames.Perforce.FromSource/Dependencies.props`, in the engine's `UnrealBuildTool/`, then `AutomationTool/`, then `AutomationTool/AutomationUtils/` and `AutomationUtils/<tfm>/`. A 5.7 or older source build has System.Linq.Async in `UnrealBuildTool/`. A 5.8 source build has no OpenTelemetry.Api until AutomationTool is built: when the engine's `IPerforceConnection.cs` uses OpenTelemetry and no `OpenTelemetry.Api.dll` exists in those folders, the build fails at once, telling the user to run `Engine/Build/BatchFiles/BuildUAT.bat` (`BuildUAT.sh` on Linux and Mac) once.
    - It is not in the solution, so `Directory.Build.props` sets `ShouldUnsetParentConfigurationAndPlatform=false` (and the reference passes `SetConfiguration`): a Release build builds it as Release.
  - The build fails with a clear message if there is neither a DLL nor the source.
  - We don't use `PerforceConnection` itself: it starts a bare `p4.exe` (which Windows looks up in the current directory first) and has no time limit.
  - The API we use, the same in 5.7 and 5.8: `PerforceSettings(IPerforceEnvironment)`, `IPerforceConnection`, `IPerforceOutput`, `PerforceRecord.FromFields`, `TryGetInfoAsync`, `TryGetChangesAsync`, `TryFStatAsync`, `TryAddAsync`, `AddOptions.IncludeWildcards` and `InfoOptions`.
- **EpicGames.Horde.** UnrealBuildTool references it, so every engine with a built UnrealBuildTool ships it prebuilt in `Engine/Binaries/DotNET/UnrealBuildTool/`, with its dependency closure (EpicGames.OIDC, Grpc, IdentityModel and the rest, as `UnrealBuildTool.deps.json` lists them). `src/AgentKit.Horde/EpicGames.Horde.props` references that DLL and closure from the same folder, imported by AgentKit.Horde and its tests only; each reference is conditional on its file, and a missing `EpicGames.Horde.dll` fails the build. It is not built from source (that needs Grpc.Tools code generation and a dozen NuGet packages). uak uses its HTTP client, its OIDC sign-in and token cache, and its request types; not its storage or compute code, so the storage code's native libraries (Oodle, e_sqlite3) are not copied.
- **Use what exists.** Where an EpicGames library already does the job, use it. Examples:
  - `EpicGames.Core`: `SingleInstanceMutex`, `ManagedProcess`, `FileReference` and `DirectoryReference`, `CommandLineArguments`, JSON helpers;
  - `EpicGames.Build`: `GlobalSingleInstanceMutex.GetUniqueMutexForPath`;
  - `EpicGames.Perforce`: its `-G` record parsing and settings, behind our `P4ProcessConnection`.
- **Version skew.** Keep the surface we use small, and build and test with every supported engine's own SDK. `ManagedProcess.Kill` is one example: 5.8 has it and 5.7 doesn't, so `ProcessRunner` disposes the process instead, which terminates it the same way.

## Command line

`uak [-project=<path>] [-engine=<path>] <command...> [arguments]`, in UE style (`-Key=Value`, case-insensitive). `uak help [command]` gives help.

Global options (`-project=`, `-engine=`, `-vcs=`, `-verbose`, `-help`) are parsed only before the command path. Everything after the command path reaches `RunAsync` verbatim, including a literal `--` and all that follows it. `uak <command> -help` shows that command's help. A partial path (`uak lock`) lists its subcommands and exits 2.

- **Commands** implement `AgentKit.Core.IUakCommand`, and parse their own arguments with `UakArguments`. The host (`UakHost`, in Core) finds implementations by reflection, so adding a command never edits the host. It searches:
  - every AgentKit.*.dll next to `uak`;
  - the folders in `UAK_COMMAND_PATHS`;
  - `<Project>/.uak/commands/`, only when opted in (see "Installing `uak`"), and lazily.

  Only assemblies that reference AgentKit.Core are loaded. A duplicate name is a warning.
- **`uak env` sections** come from the `IUakEnvReporter` implementations in the kit's own assemblies. Kit commands, `uak env`, `uak help` and `uak help <kit command>` never load other folders (`uak help <a name the kit lacks>` and `uak help -all` load the opted-in ones); when the project folder exists but isn't opted in, `help` and `env` say so in one line on stderr.
- **Exit codes:** 0 for success, 1 for failure (including a cancel), and 2 for a usage or setup error. `uak runs wait` alone also uses 3: its `-timeout` passed while the run still runs. The first Ctrl+C cancels the command; a second one kills `uak`.
- **`UAK_HOME`** (default `~/.unreal-agent-kit`) holds both the versioned installs and the fallback `State/` folder.
- **Version:** `Directory.Build.props`'s `<Version>` must equal `.claude-plugin/plugin.json`'s version.
- **Release rule:** every change that reaches users (anything under the plugin folder that an installed copy would see: agents, skills, hooks, docs or `uak` itself) bumps the version, in both files, in the same commit. Claude Code delivers a plugin update only when the version changes, and the SessionStart hook prompts a republish of `uak` only for a version that has no install yet.

## Resolving the engine and project

Core's `UakContextResolver` builds a `UakContext`.

The project comes from:
1. `-project=`;
2. the `UAK_PROJECT` environment variable;
3. otherwise, the nearest `*.uproject` found by walking up from the current directory. A directory holding more than one `.uproject` is an error that names them.

The engine comes from:
1. `-engine=`;
2. the `UAK_ENGINE` environment variable;
3. otherwise, the `.uproject`'s `EngineAssociation`, read as Unreal reads it (UE 5.8, `FDesktopPlatformBase::GetEngineIdentifierForProject` in `Engine/Source/Developer/DesktopPlatform/Private/DesktopPlatformBase.cpp`):
   - A non-empty value decides. A value holding `/` or `\` is a path, relative to the project's folder. Any other value is an identifier, looked up:
     - on Windows, in `HKCU\Software\Epic Games\Unreal Engine\Builds`, then in the launcher's installs (`HKLM\SOFTWARE\EpicGames\Unreal Engine\<version>` InstalledDirectory, then `LauncherInstalled.dat`);
     - on Linux and Mac, in UE's application settings folder (`~/.config/Epic` on Linux, `~/Library/Application Support/Epic` on Mac): `UnrealEngine/Install.ini` [Installations], then `UnrealEngineLauncher/LauncherInstalled.dat`.
   - A non-empty value that names no engine is an error, as in Unreal: `uak` never falls back to an engine that happens to contain the project.
   - An empty value means the engine root that contains the project (a source build with the project inside it). As in Unreal, the search starts at the project folder's parent and walks up.

   GUIDs match with or without braces. Unreal recognises an engine root by its `Engine/Binaries` and `Engine/Build` folders; `uak` needs `Engine/Build/Build.version` (below). The HKLM key is a `uak` addition: the launcher writes it for the same installs as `LauncherInstalled.dat`. `-project=` also accepts a directory that holds exactly one `.uproject`.

The engine root is the directory that contains `Engine/Build/Build.version`. Paths may be given as the root or as its `Engine` folder.

- **State directory:** `<Project>/Saved/AgentKit`, else `<EngineRoot>/Engine/Saved/AgentKit/Projects/<project key>` if that is writable, else `$UAK_HOME/State/<project key>`, where `$UAK_HOME` defaults to `~/.unreal-agent-kit`. Without a project, the fallbacks are `<EngineRoot>/Engine/Saved/AgentKit` and `$UAK_HOME/State/<engine key>`. A key is a hash of the normalised folder, so two projects never share a fallback folder, and neither do two engines. It also keeps state apart from the versioned `<version>/` install folders. Resolving it creates nothing; commands that write create it.
- **`uak env`** prints every resolved path, the bundled dotnet, the version-control system found, and how each was found.

## The queued lock (Locking)

`uak lock run [-name=] [-priority=High|Normal] -- <command...>` holds the lock around a command. `uak lock status` shows the holder and the queue. The library API is `await using var hold = await EditorLock.AcquireAsync(context, name, priority, ct)`.

- **The mutex itself** is an OS named mutex through `EpicGames.Core.SingleInstanceMutex`, named with `GlobalSingleInstanceMutex.GetUniqueMutexForPath("UnrealAgentKit_EditorLock", <scope key>)`, where the scope key is the scope folder's identity (see Scope), else its canonical path. The kernel releases it when its holder dies, so no dead-holder checks are needed. It is named `Global\…`, so it covers every session on the machine.
- **The queue** gives fair order and status, which the mutex lacks.
  - Waiters write tickets in `<State>/Lock/Queue/`, ordered by priority and then by arrival.
  - Only the head of the queue waits on the mutex.
  - A ticket whose process has died is removed. Liveness is checked by PID plus process start time, through `System.Diagnostics.Process`.
  - The holder writes `<State>/Lock/holder.json` for status only.
  - All files are JSON, UTF-8 without a BOM, with ISO 8601 UTC times.
- **Scope:** the mutex and the state directory derive from the canonical project directory (else the engine root), with junctions, links and subst drives resolved (Core's `CanonicalPath`). The mutex is keyed on the scope folder's identity (volume serial plus file ID; on Unix, device plus inode), so any path to the same folder, including a loopback UNC path or a mapped drive, gets the same lock. `uak lock run` refuses without a project unless `-engine-scope` is given (and refuses `-engine-scope` when a project was found). The holder logs the scope it locked, and `holder.json` records it.
- **Nesting:** while it holds, a holder sets `UAK_LOCK_HELD=<mutex>:<HoldId>` in its own environment, so its children (UBT, the editor, scripts) inherit it. A request for the same lock whose HoldId `holder.json` still records, with a live holder, joins that hold without waiting. Any other marker fails at once (lead's decision: a hold on one lock may not take another). Detached runs drop the marker.
- **Command lifetime:** `lock run`'s command runs in a kill-on-close job on Windows (a process group on Unix; TODO(unix) PDEATHSIG). Cancelling, uak dying or the command exiting stops its whole tree before release, except breakaway or detached runs. `lock run`'s job is named after the HoldId, and `holder.json` records `CommandPid`, `CommandProcessStart` and `CommandJob`. `uak build` and `uak test` record their child's PID the same way. A new holder that takes the mutex from a dead holder of the same scope waits up to 30 s, while that command lives or its job still has processes, then warns. A record of another scope (for example another project's, in a shared folder) is never waited for.
- **Versions:** the mutex name depends on the kit version's scope rule, so all agents on one machine should use the same `uak` version.
- **Tickets:** the sequence is wall-clock ticks raised above every queued ticket and this process's last, so it only grows. Another machine's tickets and holder are reported, never waited for or deleted.
- **Priority** takes effect within one poll (1 s): a High waiter takes the front from a Normal waiter already waiting there, which cancels its mutex wait and goes back in line.
- **Defaults:** an explicit priority beats `UAK_LOCK_PRIORITY`; callers pass null to inherit it. Inside a run, a lock's name shows as `<UAK_LOCK_NAME>/<name>`. `holder.json` carries a HoldId, so a holder only deletes its own record.
- **Abandonment is normal.** `SingleInstanceMutex.Dispose` doesn't wait for its own release, so every handoff looks like an abandoned mutex. Its waiters, and UBT's, treat that as success. We keep Epic's class (lead's decision, 2026-10-01). A non-kit waiter on our mutex name would see `AbandonedMutexException`.
- **One machine:** the queue is per machine, because the mutex is. The state directory must be local. Tickets and `holder.json` record `Machine`.
- **Platform check:** on Linux and Mac, .NET named mutexes work between processes of the same user. This needs testing on those platforms later. Unix doesn't delete tickets on a crash, so dead-ticket cleanup covers it there.
- **Tests:** real separate processes, checking:
  - first-in, first-out order;
  - priority;
  - a dead waiter;
  - a dead holder (the mutex is abandoned);
  - no overlap, checked through an enter/leave log;
  - cancellation.

## Detached runs (Runs)

- `uak runs start -name= -owner= [-priority=] [-result-file=] [-output=] [-force] -- <command...>` starts a run. It waits up to 30 s for the wrapper to record itself, and exits 1 if the wrapper exits first.
  - The run survives the end of the caller's shell and a restart of the agent session.
  - It is recorded in `<State>/Runs/<name>.json`, with output in `<name>.log`.
  - The run takes the lock only if the command itself does.
  - `-priority` passes `UAK_LOCK_PRIORITY` to the child, and the lock's name defaults to `UAK_LOCK_NAME`.
- `uak runs list [-all] [-name=<pattern>]` and `uak runs adopt -pid= -name= -owner= [-output=] [-result-file=]`. `runs list` shows only running runs by default and always exits 0, so it can't tell a waiter when a run ended.
- `uak runs wait -name=<exact name> [-timeout=<seconds>]` reads the run's record every 5 s until the run has ended, then prints its end state, how long it ran, its last line and its output file.
  - Exit code 0 when the run exited 0; 1 when it exited with another code, or ended with no recorded exit code (an adopted run, one that died, or one that never started), which it says; 2 for a usage error or no such run; 3 when `-timeout` passed while the run still runs.
  - A record that exists but can't be read (it is being rewritten) is read again on the next poll.
  - **How agents wait:** one background shell runs `uak runs wait -name=<name> -timeout=<seconds>`, with its own time limit longer than `-timeout` and within the shell's two-hour limit. It costs nothing while it waits, and its end wakes the agent. On exit 3, the agent starts another wait. Agents never poll a run themselves, and anything that may run over an hour goes through `uak runs start`.
- **Starting safely:** a run's name is claimed with `<Name>.claim` (create-new, delete-on-close) around the name check and the first record write, so concurrent starts of one name start one run. The output is emptied on every start. Bare program names resolve on PATH only; a missing one fails the start, and the failure is recorded.
- **Detaching:**
  - Windows: `CreateProcess` with `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB`, falling back without breakaway when the job refuses it.
  - Unix: a new session, using `setsid` through P/Invoke, or an equivalent.
- **Recording the end:** a hidden wrapper mode (`uak runs _wrap`) records the end time, exit code and last line.
  - The wrapper gets no inherited handles. It opens the log itself and hands it to the command, so no shell pipe is held and the output survives the wrapper.
  - Its own log lines start with `UakRun '`, and last-line reading skips them.
  - The wrapper retries the end record, and falls back to a log line if it still can't write it.
  - A running wrapper keeps its `uak` install's files open. That's harmless for versioned installs, but a dev build of uak can't be rebuilt while a run is going.
- **Record fields:** Name, Owner, Pid, ProcessStart, Command, Arguments, CommandLine, OutputFile, ResultFile, Priority, Adopted, Started, Ended, ExitCode and LastLine, plus the optional `WorkingDirectory` and `Detach` (`breakaway`, `job` or `session`).

## Version control (Vcs)

`IVersionControl : IDisposable` covers:
- `Kind` (Git, Perforce or None) and `RootDirectory`;
- `GetCurrentRevisionAsync` (git: commit hash; p4: the newest submitted change the workspace has);
- `GetChangedFilesAsync` (git: modified, staged and untracked files; p4: files opened in the client only, so files edited offline are not found), with an optional path filter;
- `GetFileStatusAsync(paths)`, which returns one entry per path, in the order given. The states (`VcsFileState`) are Unknown, Unmodified, Modified, Added, Deleted, Renamed, Copied, TypeChanged, Conflicted, Untracked and Ignored. Perforce never reports Conflicted, because fstat has no "unresolved" tag;
- `IsIgnoredAsync(path)` (git: `check-ignore`; p4: `add -n`);
- `DescribeAsync()` (a one-line summary for `uak env`).

Writes are Perforce only, on the side (`PerforceVersionControl`, not `IVersionControl`), and stop at shelving: nothing commits, submits or reverts (see "Perforce writes").

- **Detection,** in this order:
  1. `-vcs=`, or the `UAK_VCS` environment variable. Values: `git`, `perforce` (or `p4`), `none`. `-vcs=` is a global option that Core resolves into `UakContext.RequestedVersionControl`, and the vcs commands also accept it locally;
  2. a `.git` directory, or `.git` file, above the project;
  3. a Perforce workspace: a `P4CONFIG` file at or above the start directory. A global P4PORT or P4CLIENT alone is not used and p4 is not run; `-vcs=p4` uses them;
  4. otherwise None.

  It starts from the project directory, then the engine root, then the working directory. The API is `VersionControlDetector.DetectAsync(context)`, which returns the implementation and its provenance. `uak env` reports it through `VcsEnvReporter : IUakEnvReporter`.
- **Implementations** (git and p4 are found on PATH only, never in the current or application directory):
  - Git, through the `git` command line (`-C <root>`, `GIT_TERMINAL_PROMPT=0`, `GIT_OPTIONAL_LOCKS=0`).
  - Perforce, through `EpicGames.Perforce` (see "Engine libraries").
- **git time limit:** every git command has a time limit (`UAK_GIT_TIMEOUT`, 30 s); git is stopped and the command fails with a `VcsException` that names the variable.
- **Perforce safety:** every p4 command has a time limit (`UAK_P4_TIMEOUT`, 15 s). When detection couldn't reach the server, `uak env` reports that instead of asking again. File arguments are escaped (`@`→%40, `#`→%23, `%`→%25, `*`→%2A), a path containing `...` is rejected, and `add -n` takes the raw name with `-f`.
- **Commands:** `uak vcs status [paths...]`, `uak vcs changed [-path=]` and `uak vcs revision`, each with `-json`. Data goes to stdout and errors to the logger, so `-json` output is safe to parse.

### Perforce writes

`uak vcs edit|add|reopen <file>... [-c=<changelist>|default]`, `uak vcs change new -description=`, `uak vcs change describe -c= -description=`, and `uak vcs shelve -c=<changelist> [-replace [-drop-unopened]]` or `uak vcs shelve <file>... [-description=] [-force]`. Each takes `-json` and `-vcs=`. In a Git workspace, or with none, they are usage errors that say "Perforce only".

- **Only this client's pending changelists.** A target changelist is read with `p4 change -o` first; one that is submitted, unknown, or another client's is refused before anything changes.
- **Checked afterwards.** edit, add and reopen ask `p4 fstat -Ro` which client changelist each file ended in; exit 0 only when every file is where it was asked to be. A file p4 left elsewhere (already opened in another changelist; not on the client; ignored by P4IGNORE) is reported with p4's own message.
- **Named files.** Wildcards (`...`, `*`, `?`) are refused. Directories are refused unless `-folders`, for edit and reopen only: a directory also takes every other file opened under it. Half of a move (move/add or move/delete, from fstat's movedFile) is refused for reopen and shelve unless the other half is named too, because p4 moves both halves together.
- **Client names** compare exactly; names that differ in case alone match only when `p4 info` says the server ignores case (caseHandling). Half of a move (move/add or move/delete, from fstat's movedFile) is refused for reopen and shelve unless the other half is named too, because p4 moves both halves together.
- **Client names** compare exactly; names that differ in case alone match only when `p4 info` says the server ignores case (caseHandling). Paths must be inside the client root. add refuses files that don't exist (p4 would open them for add anyway) and passes names holding `@ # % *` raw with `-f`; the others escape them.
- **Descriptions.** `change describe` sends back the whole `p4 change -o` record (its files, jobs and every other field) with only Description replaced: a spec without its Files field moves the files out of the changelist ("updated, removing N file(s)"). A file reopened into the changelist between the read and the write is missing from that spec, so p4 still moves it out, to the default changelist: uak lists the default changelist's files before and after (`p4 opened -c default`), reopens the new ones back into the changelist, checks them, and reports them. `change new` sends Change "new", the client and the description, with no Files, so nothing moves into it.
- **Shelve.** `-c=` runs `p4 shelve -f -c<N>` (or `-r` with `-replace`, which makes the shelf exactly the opened files) after checking that something is opened, since `-r` with nothing opened would empty the shelf. uak reads the shelf before and after (`p4 describe -S -s`): `-r` deletes shelved files that are not opened in the changelist, and a shelf can be the only copy of that work, so `-replace` is refused, listing those files, unless `-drop-unopened`; every file the shelf loses is printed, and so is every shelved file whose content changed (its digest). With files instead, uak first checks every file is opened here (or changes nothing), refuses files in a numbered changelist (they would leave it; `-force` moves them) and half moves, then creates a changelist, reopens the files into it, checks them, and shelves it; it prints each file's original changelist. Shelving may take up to 30 minutes (`PerforceVersionControl.ShelveTimeout`), because it sends file contents; queries keep `UAK_P4_TIMEOUT`.
- **Records.** EpicGames.Perforce has no typed record for some of these answers (its ReopenRecord has no fields), so `PerforceRawRecord` reads every field of each record through the library's own `PerforceOutput.ParseRecord`, and writes specs back with `PerforceRecord.Serialize`.
- **Tests** use recorded answers of a real p4d (fixtures), and an opt-in test runs every write against a throwaway local p4d over `rsh:` (`UAK_TEST_P4D`, the path of a p4d program).

## Horde

`uak horde config|login|logout|streams|templates|preflight|job`, in AgentKit.Horde, on EpicGames.Horde (see "Engine libraries").

- **The server** comes from `-server=`, then `<UAK_HOME>/config.json`'s `horde.server` (shared by every kit version; `uak horde config -server=` writes it, atomically, keeping other settings), then Horde's own default (`UE_HORDE_URL`, the registry value Horde's tools write on Windows, `~/.horde.json` elsewhere). With none, the commands fail with a message telling the agent to ask the user (through the lead) for the URL.
- **Sign-in.** A command first gets a token silently (Horde's token cache, refreshed without a prompt; `AllowAuthPrompt` off). When that fails, it opens the Horde sign-in page in the browser, in that process, and waits up to `-login-timeout` (600 s), then exits 4. `-no-login` never opens it. The sign-in waits on the library's auth state directly (`GetAccessTokenAsync(true)`), not inside an HTTP request, so HttpClient's 100 s time-out doesn't cut it short: `-login-timeout` is the real limit. EpicGames.OIDC stores only the refresh token, so if the identity provider rejects the silent refresh (for example Entra ID's AADSTS90009), each process would sign in again; uak's token cache (below) bridges that, and the user should ask the Horde admin to check the server's OIDC scope. `uak -verbose` shows the library's own messages.
- **Stream and template.** The workspace's client stream, then its parents up through virtual streams to the first non-virtual one (a shelf holds that stream's paths), matched as Horde's dashboard does: by name, then by id (the name without `//`, `/` as `-`, lower case), then by name plus `-VS`. The template: `-template=` (id or name, must allow preflights), else the stream's default preflight template, else its only runnable preflight template. Streams and templates are read into uak's own records: the library's response classes can't be deserialized.
- **Starting a preflight.** EpicGames.Horde's own `CreateJobRequest`, serialized with its own settings: `PreflightCommitId`, `UpdateIssues = false`, `AutoSubmit` only with `-autosubmit` (which tells Horde to edit the change's description and submit it when the preflight succeeds; the usage text and output say so). Job creation isn't idempotent and the library's HTTP handler retries, so the POST goes once through a plain HttpClient with the bearer token. Before shelving or creating anything, uak lists the change's preflights. With `-shelve` it never reuses one (a new shelf needs a new build), and refuses to shelve while an auto-submit preflight of the change is running (a new shelf could change what Horde submits). Without `-shelve` (and without `-force`) it reports a still-running preflight instead of starting another ("reused: <job>") only when it has the same stream, template, auto-submit setting and every parameter value (the job's reported parameters against the template's defaults with uak's values over them) and was created after the change was last shelved (`p4 change -o`'s shelveUpdate, in the server's time zone); when any of that can't be read, it starts a new one. The line naming the template and parameters, and the auto-submit state, describe the job actually reported. After a create that fails without a clear answer, uak looks for the job before reporting the failure.
- **The job URL** (`<server>/job/<id>`) is printed as "Job URL: ..." the moment the job is created or found, before any wait (standard error under `-json`), so it survives a failure, a time-out or Ctrl+C. Agents send it to the lead at once.
- **Results** follow Horde's own rule (JobExtensions.GetTargetState): the latest step per node; any failure fails the job, else warnings, else skipped or aborted steps (or a batch that couldn't run) leave it incomplete, else success. The summary lists only failing steps and those with warnings, with their step and log URLs; `-verbose` lists every step.
- **Waiting** (`-wait`) polls with `modifiedAfter` (an unchanged job is an empty answer), every 15 s, backing off to 60 s while nothing changes; prints one line when the job starts running and one when Horde can't be reached; survives transient errors (no connection, time-outs, 408, 429, 5xx); and stops at `-timeout`. After 4 transient failures in a row it rebuilds its client (a fresh connection, same sign-in). A time-out before any poll got an answer is "still running" too (3). Exit codes: 0 success, 1 failure or incomplete, 2 usage error (uak's own, before anything is sent), 3 still running, 4 not signed in, 5 another error (including 403, not allowed, and any answer uak can't read: never mistaken for a job result), 6 (preflight) build settings needed, 7 success with warnings.
- **Build settings.** A preflight's template and parameter values are saved per user, server and Horde stream, in `<UAK_HOME>/horde/<server>/<stream>/templates.json` (`<server>` is the host, then the port and path segments joined by `_`): the chosen template and each template's values, written atomically, keeping keys uak doesn't know. `uak horde config -template= -param:<id>=` saves them after checking them against the server's template (ids, bool values, list choices, a text's regular expression); `-reset-build` forgets them. Parameters come from the stream list's templates (Bool, Text and List; a List has no id in Horde, only its choices, so uak names it from its label). Values go to Horde as `CreateJobRequest.Parameters`: a bool or text by id, a list as every choice's id with "true" or "false". With nothing saved and no `-template=`/`-param:`, or with saved settings that no longer fit the server's templates, a preflight starts nothing and exits 6, printing the templates as `uak horde templates -json` does, so the lead can ask the user (agents can't answer prompts on standard input). `-use-template-defaults` skips this for CI.
- **Token cache.** EpicGames.OIDC keeps only refresh tokens; when a server refuses refreshes, every process would sign in again. So on Windows uak keeps the access token per server in `<UAK_HOME>/horde/<server>/token.bin`: JSON with the server and the expiry (the JWT's `exp`; no expiry, no cache), encrypted with DPAPI for the current user (CryptProtectData, the server's URL as entropy), written atomically (last writer wins). It is passed as `HordeOptions.AccessToken` while more than 5 minutes are left. A 401 on it deletes it, signs in afresh, and retries only the request that failed (`HordeApiSession`): a preflight is never shelved or created twice, and a wait keeps its job and its deadline. A token EpicGames.Horde takes from the environment (`UE_HORDE_TOKEN` for the `UE_HORDE_URL` host) is neither replaced by the cache nor saved in it. `uak horde login` checks a cached token with the server before saying it is signed in. An unreadable file is ignored and replaced. `uak horde logout` deletes it. Other platforms cache nothing. The token is never printed or logged.
- **Opening pages.** `horde.open` in the config (`uak horde config -open=never|created|finished|failed`, default never) opens the job's page, or the first failed step's, in the default browser during `-wait`; `-no-open` skips it once. A failure to open is one warning.

## Unreal operations (Unreal)

- **`uak compile <file>... [-dependents] [-target=] [-config=] [-platform=] [-MaxParallelActions=] [-maxerrors=] [-resultfile=] [-- <UBT option>...]`** checks files through UBT's single-file compile (`-SingleFile=`, given as a file list) for the project's editor target (`<Project>Editor`, else the `Editor` target in `Source/*.Target.cs`, else `UnrealEditor`), with the platform's real compiler. A `Type = TargetType.Editor` inside a `//` or `/* */` comment doesn't count. Nothing is linked, and objects go to a separate `SingleFile/` folder, so it is safe while an editor runs.
  - UBT compiles a header on its own, through a generated `<Name>.h.cpp`, so headers must be self-contained (lead's decision, 2026-10-01: keep this strict default). `-dependents` adds `-SingleFileBuildDependents`.
  - UBT silently drops a file that isn't in the target, or a header marked HEADER_UNIT_SKIP, and skips a file whose `SingleFile` object is newer. So uak first deletes the requested files' old `SingleFile` objects (only under the project folder), then checks that a compile action ran for each file. A file that didn't compile is NOT COMPILED, exit code 2.
  - The files go to UBT in a list file, `-FileList=<State>/Logs/compile-<UTC time>-<pid>.files.txt` (one full path per line, UTF-8, kept beside the log), not as one `-SingleFile=` each (since 0.2.4). UBT reads the list into the same list `-SingleFile=` fills, before `-SingleFileBuildDependents` expands it, so it is one UBT run, as before, for any number of files. One option per file broke on a source build: about 150 files passed cmd.exe's 8191-character limit for `Build.bat` ("The command line is too long"). UBT reads no `@response` file, and splitting into batches would run UBT, and with `-dependents` the dependents, once per batch. `-FileList=` reads the same in UE 5.5, 5.6 and 5.7.
  - It passes `-WaitMutex`, and does not take the editor lock. UBT's own mutex already makes compile checks run one at a time.
  - UBT options after `--` are checked as for `uak build`. `-SingleFile`, `-File`, `-Files`, `-FileList` and `-SingleFileBuildDependents` are rejected too: uak sets them, and UBT reads the first four into one list of files, which would add files uak does not report on.
  - It prints errors as `file(line): error: CODE: message`. A header error repeated across translation units is reported once, with a count.
- **`uak build [-target=] [-config=Development] [-platform=host] [-MaxParallelActions=] [-maxerrors=] [-resultfile=] [-- <UBT option>...]`** runs under the editor lock. On an installed engine it runs UBT directly on the bundled dotnet; on a source build it runs the platform's Build script, which rebuilds UBT when needed. It always adds `-WaitMutex` and `-NoHotReloadFromIDE`.
  - **UBT options after `--`** (for example `-DisableAdaptiveUnity` or `-Module=<name>`) go to UBT as given, after uak's own (`UbtCommandLine.CheckPassThrough`). Each must be an option: UBT reads a bare word as another target, platform or configuration.
  - Options uak sets, or that would break its guarantees, are rejected: `-Project`, `-Target`, `-TargetList` and `-Mode` (what is built, and that it is a build); `-WaitMutex` and `-NoMutex` (UBT's one-instance mutex); `-NoHotReloadFromIDE`, `-ForceHotReload` and `-LiveCoding` (never patch a running editor); and `-MaxParallelActions` (use uak's option). Names match in any case, as UBT reads them. A rejected option is a usage error (exit 2), before the lock is taken.
- **`uak test -filter=<prefix> [-gpu | -windowed [-resx=] [-resy=]] [-name=] [-resultfile=] [-- <editor argument>...]`** runs the editor with `-ExecCmds="Automation RunTests <filter>;Quit"` (`-nullrhi` unless `-gpu` or `-windowed`) under the lock.
  - **Windowed runs** (`-windowed`, for tests that need a real viewport and Slate windows) start the editor itself, not its `-Cmd` (`EditorLocation.Executable`: the receipt's `Launch`, else the editor beside the `-Cmd` one). They pass `-windowed -ResX=1600 -ResY=900` (`-resx=` and `-resy=` change the size) instead of `-nullrhi` or `-RenderOffscreen`.
    - A window opens on the desktop, so it needs an interactive session.
    - Pass and fail are decided as for any run: from the log (`-abslog`), the report and the `-testexit` exit.
    - On Windows the windowed editor is a GUI program and writes nothing to its console, so a crash before the log has no last output to show.
    - `-gpu` and `-windowed` together, or `-resx=`/`-resy=` without `-windowed`, are usage errors.
  - **Editor arguments after `--`** (for example `-SCCProvider=None`) go last, as given (`EditorTestCommandLine.CheckPassThrough`). Those uak sets are rejected, written `-Name`, `--Name` or `/Name`, in any case:
    - `-ExecCmds`, `-testexit`, `-abslog` and `-ReportExportPath`: uak's checks depend on them, and the engine reads only the first of each (FParse::Value), so a second one would be ignored silently;
    - `-nullrhi`, `-RenderOffscreen`, `-windowed`, `-ResX` and `-ResY`: these are uak's `-gpu`, `-windowed`, `-resx=` and `-resy=`.
  - **Which editor:** the project editor target's build environment decides it (`EditorLocator`, Core).
    - A shared environment, the default for a modular editor, runs the engine's `UnrealEditor-Cmd`.
    - A unique one runs `<Target>-Cmd` from the project's `Binaries/<Platform>`. UEBuildTarget.cs names the binaries after the target, and puts them under the project when the `.Target.cs` is there.
  - **Why the receipt:** the `.Target.cs` text can't tell the two apart. A base class in another file can set the environment, as can `-UniqueBuildEnvironment`, and UBT settles `UniqueIfNeeded` only by compiling the rules.
    - So uak reads the target receipt UBT writes when it builds the editor, `<Project or Engine>/Binaries/<Platform>/<Target>.target`. Its `TargetBuildEnvironment`, `Launch` and `LaunchCmd` are UBT's own answer.
    - Without a receipt, uak takes a built `<Target>-Cmd` if there is one, then a `BuildEnvironment = TargetBuildEnvironment.Unique` in the target file itself, then the shared editor.
    - `uak env` shows both editors (cmd and windowed) and how they were found.
    - UBT's `-Mode=JsonExport` would also answer, but it compiles the rules and builds the target graph under UBT's mutex: too slow for every run.
  - The log is `<Project>/Saved/Logs/<name>.log`, deleted before the run. The report goes to `<State>/TestReports/<name>`.
  - A run passes only when all of these hold: tests were found; the found count equals the completed count; none failed; at least one passed (all skipped is NOTHING RAN); the queue finished; and the editor exited 0. A non-zero editor exit fails the run even when every test passed (lead's decision: keep it strict, and print the editor's exit code next to the test counts).
- **Logs:** compile and build write UBT's output to `<State>/Logs/<command>-<UTC time>-<pid>.log`. UBT and Build-script output is decoded with `ProcessRunner.ConsoleEncoding` (the OEM code page on Windows).
  - Both print `Log: <path>` first, and flush the log line by line, so a long run can be followed while it goes.
  - Both echo UBT's latest `[n/N]` action line, with the time so far, at most once a minute (`UbtProgress`, on `UnrealServices.Clock`), so a long build is visibly alive. A run shorter than a minute echoes nothing.
- **`-filter`** rejects `,` `;` `|` quotes, backticks and white space, which would break the `-ExecCmds` list.
- **Headers under `-dependents`:** the header counts as compiled only through its own `<Name>.h.cpp` (or `.h.obj`) action. The other compiled units are reported on their own lines and never stand in for it.
- **Other rules:**
  - Paths to UBT, the editor and scripts come from the engine directory and the host platform. Never hard-code `.exe` or `Win64`; use a helper in Core.
  - Parallelism is capped by `-MaxParallelActions` when given, else by `UAK_MAX_PARALLEL_ACTIONS`.

## Plugin content

- **Agents** (named `unreal-agent-kit:<name>` in Claude Code; before 0.3.0 they were `ue-<name>`, see CHANGELOG.md):
  - `low`, running on Sonnet;
  - `medium`, `high` and `xhigh`;
  - `runner`, on Sonnet at low effort, which cannot edit: it runs a given list and reports, so a verification never changes what it verifies;
  - `research`, read-only at high effort, for engine investigations answered with `file:line` evidence. Its reading never conflicts with workers' edits, but its experiments queue like any run: `uak test` takes the editor lock, and `uak compile` waits for UBT's mutex. `uak compile` only compiles files inside a module, so an experiment compiles existing project files only; one that needs new code asks the lead, and a worker writes it (a file a researcher added to a module would be picked up by other agents' builds);
  - `review`, read-only;
  - `architect`, at high effort, which drafts a milestone's plan, briefs README and epic briefs from the skill's templates, writing only the draft files the lead names; it runs nothing and never edits code.
- **Their ground rules:**
  - never spawn agents (`disallowedTools: Agent`);
  - never kill processes or commit unless told;
  - take the lock for editor and build runs;
  - start anything that may run over an hour detached through `uak runs start`, and wait for it with one background `uak runs wait`;
  - ask the lead with SendMessage to "main";
  - ask for a runner for long, low-judgment work;
  - follow epic briefs, and rewrite their Current state and Next step at every hand-back, so the lead can respawn a fresh agent from the brief instead of resuming a large context;
  - no new batch or shell scripts: tools go in `uak`.
- **The workflow skill:** briefs (a README plus E#.md per milestone), caps, effort tiers, delegation (runner and research requests), when to resume an agent and when to respawn it from its brief, reviews, spec-doc sync, and an opt-in usage-limit watchdog (only if the project's owner allows auto-resume).
- **Docs:**
  - the install steps;
  - recommended settings: `CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP=1`, the auto-compact window;
  - a CLAUDE.md snippet;
  - how to build `uak` on first use.
- **SessionStart hook** (`hooks/hooks.json`, `hooks/check-uak.sh`): at startup, only in an Unreal project (a `.uproject` at or above the project folder, or an engine root), it checks for `$UAK_HOME/<plugin version>/uak`. When that is missing it shows the user a one-line notice, and tells Claude to ask them with AskUserQuestion, before acting on their first message, whether to publish now. A hook can't start a turn itself, so this is the earliest the question can come. A plugin update therefore prompts a republish. It only reads files, always exits 0, and is POSIX `sh`, because it must run on every user's machine before `uak` exists (Claude Code runs hooks in Git Bash on Windows, so it needs Git for Windows there; the docs say so). Publishing from the installed plugin copy is fine: only its throwaway `tools/bin` and `tools/obj` are written there, and the install goes to `$UAK_HOME`.
  - The walk up for a `.uproject` stops before the filesystem root and after 40 levels, and never searches a UNC host: in Git Bash, `/` globs as `//*`, which browses the network for seconds.
  - On Windows, paths are converted to POSIX form with `cygpath -u` when it exists, and the default `UAK_HOME` is under `USERPROFILE`, as `uak` itself finds it, not Git Bash's `HOME`.
  - **The command.** It finds the project's engine as `uak` does, as far as `sh` can: `UAK_ENGINE`; else the `.uproject`'s `EngineAssociation` (a path, relative to the project; empty, the engine containing the project; a version, the launcher's default install folder on Windows and Mac). It then takes that engine's newest bundled SDK for this host, so the command it gives is exact. A registered build's GUID needs the registry, so then it gives the generic command and Claude finds the engine.
  - **Old versions.** When older version folders are installed, the question offers to remove them after a successful publish, folder by folder (`rm -rf` on each listed version, never `State/`). A folder a detached run still holds open fails to delete and is left.
  - **Bash, not PowerShell.** The commands it gives are `sh` (a `VAR=value` prefix, `/c/`-style paths on Windows), so it tells Claude to run them with its Bash tool, never PowerShell or cmd.
  - A test runs it through `sh` (`HookTests`): the one on PATH, else, on Windows, Git for Windows' `bin/sh.exe` beside `git.exe` (PowerShell and cmd usually have only Git's `cmd` folder on PATH), so the hook tests run there too.
- **`uak` on PATH** (`bin/uak`, `bin/uak.cmd`): Claude Code puts an enabled plugin's `bin/` folder on the PATH of the shell it runs commands in, after the user's own entries. Each shim runs `$UAK_HOME/<plugin version>/uak` with its arguments as given, and exits with its exit code. The version comes from the plugin's own `plugin.json`, and the home is found as the hook finds it (`USERPROFILE` on Windows). So a plugin update moves every session to the new version's `uak`, with no PATH change. With no published `uak`, a shim prints one line that points to docs/INSTALL.md and exits 2, uak's setup-error code.
  - `bin/uak` is POSIX `sh`, for Git Bash, Linux and Mac; git keeps its executable bit. `bin/uak.cmd` is for PowerShell and cmd, which find it through PATHEXT and skip the extensionless file. `uak lock run` and `uak runs start` find `uak.cmd` the same way.
  - `uak.cmd` passes `%*` on through cmd, which may change `%`, `^` and `!` outside quotes. For such arguments, call `uak.exe` by its full path.
  - A `uak` the user put on PATH comes first, so after an update it must point at the new version's folder, or go.
  - The Claude Code docs promise `bin/` for the Bash tool. claude.ai and Cowork don't install a plugin with a top-level `bin/`.
  - `HookTests` runs both shims against a stand-in published `uak`: the test child's launcher, which the test project's build copies to `FakeUak/0.0.0-shim/uak[.exe]`.

## Shared contracts

- `IUakCommand` and `UakContext` (in Core) are what every other library, and every project command (see "Installing `uak`"), builds against.
- Keep changes to them additive. Project commands are built separately from the kit, so a breaking change would break them silently.
