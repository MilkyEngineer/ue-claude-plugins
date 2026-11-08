# uak: building, testing and installing

`uak` is built with the .NET SDK that ships inside Unreal Engine. It links the engine's own prebuilt `EpicGames.*` libraries and the third-party libraries beside them, and targets the engine's .NET version, so no other .NET install and no NuGet package is needed to build it. Every command below is one line, run from the kit's `tools` folder, and uses the engine's `dotnet` (`<version>` is `10.0` in UE 5.8, `8.0.412` in UE 5.7):

- Windows: `<Engine>/Engine/Binaries/ThirdParty/DotNet/<version>/win-x64/dotnet.exe` (`win-arm64` on Arm).
- Linux: `<Engine>/Engine/Binaries/ThirdParty/DotNet/<version>/linux-x64/dotnet` (`linux-arm64` on Arm).
- Mac: `<Engine>/Engine/Binaries/ThirdParty/DotNet/<version>/mac-arm64/dotnet` (`mac-x64` on Intel).

Set `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1` first if you like.

## Which engine the build uses

`build/EpicGames.props` finds the engine in this order:
1. `-p:UakEngineDir=<engine root>`;
2. the `UAK_ENGINE` environment variable;
3. the engine whose bundled `dotnet` runs the build.

So running the engine's own `dotnet` needs no setting. A `UAK_ENGINE` left set in your environment, or in a project's `.claude/settings.local.json`, beats the `dotnet` you run: the next build or publish uses that engine. With any other `dotnet` and no engine given, the build stops with an error that says what to set. The build's output goes only to `tools/bin` and `tools/obj`. NuGet is used only by the tests (MSTest) and by a self-contained publish (the .NET runtime pack), which go to your user NuGet cache. The bundled SDK may still update its own metadata folder under the engine's `Binaries/ThirdParty/DotNet` directory, as any dotnet does on first use.

## Build and test

Run these from `tools/`, not from the plugin root: `global.json`, which selects the test runner, is only found from here.

```
<dotnet> build UnrealAgentKit.sln
<dotnet> test --solution UnrealAgentKit.sln
```

With UE 5.7's SDK (8.0), which predates `--solution`, the test command is `<dotnet> test UnrealAgentKit.sln`.

## Install (publish)

```
<dotnet> publish src/uak -c Release
```

- **What it makes.** A self-contained `uak` for this machine. It needs no installed .NET, and no `DOTNET_ROOT`.
- **Where it goes.** To `$UAK_HOME/<kit version>/`, where `UAK_HOME` defaults to `~/.unreal-agent-kit`. The kit version is the `version` in `.claude-plugin/plugin.json` (and `<Version>` in `Directory.Build.props`). For kit version 0.4.3: `~/.unreal-agent-kit/0.4.3/uak.exe` on Windows, `~/.unreal-agent-kit/0.4.3/uak` elsewhere.
  - The folder is outside the plugin, which Claude Code replaces on every update.
  - Each kit version gets its own folder, so publishing never overwrites a `uak` that is running.
- **What to do next.** In Claude Code sessions, the plugin's `bin/` puts it on `PATH`. Elsewhere, put that folder on `PATH`, or call `uak` by its full path. Then check it with `uak env`.
- **Overrides.**
  - `-r <rid>` builds for another platform.
  - `-o <folder>` publishes somewhere else.
  - `-p:SelfContained=false` makes a smaller build that needs `DOTNET_ROOT` set to the engine's `dotnet` folder.

## Single engine dependencies (`uak gitdeps`)

A source engine gets its binary dependencies from GitDependencies (`Setup.bat`), which can leave files out: an `-exclude=` folder, or a `.gitdepsignore`. `uak gitdeps status <path-or-glob>...` says whether files the engine's `*.gitdeps.xml` manifests list are present, outdated, modified or missing, and whether GitDependencies' working manifest (`.uedependencies`) tracks them; `uak gitdeps fetch <path-or-glob>... [-dry-run] [-force] [-max-mb=<n>]` downloads only the packs that hold those files, checks each file's SHA-1, writes them and records them, without running GitDependencies. Quote a glob (`'Engine/**/IOS/*.png'`) so Git Bash and PowerShell pass it through, and give a name that starts with a dot as `./.name`.

- **Root.** `-root=<engine root>`, else the engine `uak` resolves (`uak env`); the output says which. An installed engine has no manifests. A manifest that names a file outside the root (rooted, or with a `..` segment) is refused.
- **States.** Present: the manifest's SHA-1. Outdated: tracked, with the hash `.uedependencies` expected but not the manifest's (the engine changed since GitDependencies wrote it); GitDependencies replaces it without asking, and so does a fetch. Modified: tracked with a hash other than the one `.uedependencies` expected, or untracked with a hash other than the manifest's; a fetch refuses it, before any download, unless `-force`. A tracked file whose write time is the saved one keeps its saved hash, as GitDependencies does.
- **Packs.** The URL is `<BaseUrl>/<RemotePath>/<pack hash>`, from the manifest; a pack is a gzip stream of blobs laid end to end, and a blob's `PackOffset` and `Size` are in the uncompressed stream. A fetch reads each pack only up to its last needed blob, and a file of zero bytes needs no pack. It reads GitDependencies' download cache when a pack is in it (`-cache=`, else as GitDependencies finds it: `-cache=` in `UE_GITDEPS_ARGS`, `UE_GITDEPS`, then `.git/ue-gitdeps`, where an empty `-cache=` in `UE_GITDEPS_ARGS` skips `UE_GITDEPS`; never written; `-no-cache` turns it off). The proxy is found as GitDependencies finds it, for every URL, https included: `-proxy=`, else `-proxy=` in `UE_GITDEPS_ARGS`, else `HTTP_PROXY`, else `http_proxy`; credentials come from the URL, else from `UE_GITDEPS_ARGS`' `-proxy-user=` and `-proxy-password=`. Give credentials in `UE_GITDEPS_ARGS` or `HTTP_PROXY`, never in uak's own `-proxy=`: uak's command line shows in `uak runs list`. The output never shows credentials, and an error about the proxy names only where it came from, never its value.
- **Retries and time limits.** A failed read of a pack (an HTTP error other than 4xx, or a 408 or 429; a broken connection; no data for 100 s; or a transfer longer than GitDependencies' time limit for that pack) is tried again after 1 s, then 2 s, or after the server's `Retry-After` in seconds (at most 30 s) when that is longer. A failure writing a file is not retried; the output names the path.
- **Writes.** Every file goes to a temporary file beside it, and all are renamed into place only once every hash is checked: a failure before the renames writes nothing. If a rename fails, the files already renamed stay (each verified), the output names them, the other temporary files are deleted, and the exit code is 1. Once the renames start they finish, and a cancel takes effect after the files are listed and recorded. Temporary files are deleted on every way out, a cancel included. On Mac and Linux, a failure to set a file's execute bits is reported as such (exit code 1; the file stays). `-max-mb=` (default 512) caps the download, counting each pack's whole compressed size (an upper bound), and a cached pack that turns out unusable counts before it is downloaded; `-dry-run` shows the packs and their compressed bytes and writes nothing.
- **Tracked.** When `.uedependencies` exists, each fetched file is recorded in it as GitDependencies records what it downloads (Hash and ExpectedHash the manifest's, Timestamp the file's write time, written through `.uedependencies.tmp`), and so is each selected file already present with the manifest's hash but untracked, as GitDependencies records such a file (the output counts them apart); every other entry stays as it was. GitDependencies then treats the files as its own and updates them with the engine. For a folder its exclusions cover, it drops the entry on its next run and leaves the file, which an engine update can then leave out of date (shown as modified; fetch it again with `-force`). With no `.uedependencies` (or only the legacy `.ue4dependencies`) the files stay untracked, and the output says so (and, when a `.uedependencies.tmp` is there instead, that it holds an update to the manifest not yet moved into place, which Setup recovers). If it can't be read or written, the files stay, untracked, with one warning; a failed write deletes its `.uedependencies.tmp` (one GitDependencies' own next write could fail on), or the warning names it, to delete before running Setup. `status` reads `.uedependencies` sharing delete, so it never makes a writer's delete fail. A fetch refuses to start while a process named GitDependencies runs.
- **One fetch per engine.** A fetch that writes (not `-dry-run`) holds a system-wide lock named after the engine root folder for its whole run, whatever path reaches it (a junction or link, a subst drive, another case); another `uak gitdeps fetch` on the same engine is refused at once (exit code 2), as is a fetch whose lock another user's or an elevated fetch holds. A lock a killed fetch left is taken over, and the fetch first deletes the temporary files (`<file>.uak-<32 hex digits>.tmp`) such a fetch can leave, only beside the selected files, and names them.
- **Output.** Each state lists at most 50 files, then a count of the rest; `-all` lists every file, and `status -json` prints every file as JSON.

Neither takes the editor lock; both are safe while builds run.

## Project commands

A project can add its own `uak` commands without changing the kit:
1. Build a class library that references `AgentKit.Core.dll` and implements `AgentKit.Core.IUakCommand`. Target a .NET no newer than `uak`'s, which is the .NET of the engine `uak` was published with (`net10.0` for UE 5.8, `net8.0` for UE 5.7); `net8.0` loads in both. `uak env` shows `uak`'s .NET and engine.
2. Put its DLL in `<Project>/.uak/commands/`, or in a folder listed in `UAK_COMMAND_PATHS` (separated by `;` on Windows, `:` elsewhere). List absolute folders only: a relative entry is skipped with a warning.
3. `<Project>/.uak/commands/` is off by default. Turn it on with `UAK_PROJECT_COMMANDS=1` (or `true`), or by listing it in `UAK_COMMAND_PATHS`.

Loading a command runs its code, so these folders load lazily: only to run a command the kit does not have, or for `uak help -all`, which lists the new commands. The host loads only the DLLs in those folders that reference `AgentKit.Core`. The other DLLs there are loaded when a command needs them.

Windows PowerShell 5.1 splits an argument at a dot (`-key=a.b` arrives as `-key=a` and `.b`). A command's `new UakArguments(arguments)` takes the tokens as they came, as it did in 0.4.1. To have them joined, pass a rule: `new UakArguments(arguments, DotSplitRule.Options)`. `DotSplitRule.Paths` is for a command that takes paths as positional arguments, so `.\Foo.cpp` after an option stays a path, and `DotSplitRule.Command` for one whose words start a command, which reads no disk and refuses a dotted word it can't place. A command that hands arguments on to another tool reads `PassThrough`, which `Rest`'s verbatim tokens are joined for.

**Trust:** a command DLL runs as you, with your rights. Turn project commands on only for a project whose `.uak/commands/` you trust as much as its build scripts.
