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
- **Where it goes.** To `$UAK_HOME/<kit version>/`, where `UAK_HOME` defaults to `~/.unreal-agent-kit`. The kit version is the `version` in `.claude-plugin/plugin.json` (and `<Version>` in `Directory.Build.props`). For kit version 0.3.1: `~/.unreal-agent-kit/0.3.1/uak.exe` on Windows, `~/.unreal-agent-kit/0.3.1/uak` elsewhere.
  - The folder is outside the plugin, which Claude Code replaces on every update.
  - Each kit version gets its own folder, so publishing never overwrites a `uak` that is running.
- **What to do next.** In Claude Code sessions, the plugin's `bin/` puts it on `PATH`. Elsewhere, put that folder on `PATH`, or call `uak` by its full path. Then check it with `uak env`.
- **Overrides.**
  - `-r <rid>` builds for another platform.
  - `-o <folder>` publishes somewhere else.
  - `-p:SelfContained=false` makes a smaller build that needs `DOTNET_ROOT` set to the engine's `dotnet` folder.

## Project commands

A project can add its own `uak` commands without changing the kit:
1. Build a class library that references `AgentKit.Core.dll` and implements `AgentKit.Core.IUakCommand`. Target a .NET no newer than `uak`'s, which is the .NET of the engine `uak` was published with (`net10.0` for UE 5.8, `net8.0` for UE 5.7); `net8.0` loads in both. `uak env` shows `uak`'s .NET and engine.
2. Put its DLL in `<Project>/.uak/commands/`, or in a folder listed in `UAK_COMMAND_PATHS` (separated by `;` on Windows, `:` elsewhere). List absolute folders only: a relative entry is skipped with a warning.
3. `<Project>/.uak/commands/` is off by default. Turn it on with `UAK_PROJECT_COMMANDS=1` (or `true`), or by listing it in `UAK_COMMAND_PATHS`.

Loading a command runs its code, so these folders load lazily: only to run a command the kit does not have, or for `uak help -all`, which lists the new commands. The host loads only the DLLs in those folders that reference `AgentKit.Core`. The other DLLs there are loaded when a command needs them.

**Trust:** a command DLL runs as you, with your rights. Turn project commands on only for a project whose `.uak/commands/` you trust as much as its build scripts.
