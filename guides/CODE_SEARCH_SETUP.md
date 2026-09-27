# Code search setup for an Unreal Engine project (Claude Code, Windows)

Instructions for a Claude Code agent to reproduce this setup on another machine or project.
Goal: fast, low-memory code navigation for a UE project that sits on a huge engine (~80k files),
without an engine-wide knowledge graph (multi-GB) or WSL/Docker-based indexers (too much memory).

End state:
- **clangd** through the official `clangd-lsp` Claude Code plugin, for C++ symbol navigation
  (definition, references, hover, call hierarchy) across project code and every engine header it includes.
- **graphify** (optional; only if the user already uses it) indexing **only the project's own code**.
- Guidance in `.claude/CLAUDE.md` telling agents which tool to use for what.

## Parameters

Work these out first; ask the user only if you can't.

| Name | How to find it | Example |
|---|---|---|
| `PROJECT_DIR` | Folder containing the `.uproject` | `C:\Users\alex\Documents\Unreal\CodeProject` |
| `PROJECT_NAME` | `.uproject` file name without extension | `CodeProject` |
| `EDITOR_TARGET` | `Source/<Name>Editor.Target.cs` | `CodeProjectEditor` |
| `UE_ROOT` | Engine install. `EngineAssociation` in the `.uproject` gives the version; default path is `C:\Program Files\Epic Games\UE_<ver>` | `C:\Program Files\Epic Games\UE_5.8` |

Check that `"%UE_ROOT%\Engine\Binaries\DotNET\UnrealBuildTool\UnrealBuildTool.exe"` exists.
Installed (launcher) engines don't ship `RunUBT.bat`, so call `UnrealBuildTool.exe` directly.

## 1. Install clangd (LLVM)

```powershell
winget install --id LLVM.LLVM -e --accept-package-agreements --accept-source-agreements --silent
```

- The download is ~400 MB, so run it in the background.
- **winget does not add LLVM to PATH.** Append it to the user PATH, or the plugin can't start clangd:

```powershell
$p = [Environment]::GetEnvironmentVariable('Path','User')
if ($p -notlike '*C:\Program Files\LLVM\bin*') {
  [Environment]::SetEnvironmentVariable('Path', ($p.TrimEnd(';') + ';C:\Program Files\LLVM\bin'), 'User')
}
```

- Verify with `& 'C:\Program Files\LLVM\bin\clangd.exe' --version`.

## 2. Install the Claude Code plugin

```bash
claude plugin install clangd-lsp@claude-plugins-official
```

- It runs `clangd --background-index`.
- It only takes effect after Claude Code restarts, which also picks up the new PATH.

## 3. Generate `compile_commands.json`

Create `PROJECT_DIR\clangd_setup.bat` (substitute the parameters):

```bat
@echo off
REM Generate compile_commands.json for clangd (used by the clangd-lsp Claude Code plugin).
REM Re-run after adding modules, source files, or changing Build.cs dependencies.
REM Requires LLVM (winget install LLVM.LLVM) so UBT can use the Clang toolchain.

setlocal
if not defined UE_ROOT set "UE_ROOT=C:\Program Files\Epic Games\UE_5.8"

"%UE_ROOT%\Engine\Binaries\DotNET\UnrealBuildTool\UnrealBuildTool.exe" -mode=GenerateClangDatabase ^
    -project="%~dp0CodeProject.uproject" CodeProjectEditor Win64 Development ^
    -OutputDir="%~dp0."
```

Run it. In PowerShell use the full path (`& '<PROJECT_DIR>\clangd_setup.bat'`), because `cmd /c name.bat` from PowerShell fails with "not recognized". It takes seconds and should end with `Result: Succeeded`.

Things to know:
- **`-OutputDir` is required.** The default is the engine root, which is under Program Files and not writable.
- **Write `"%~dp0."`, not `"%~dp0"`.** `%~dp0` ends in `\`, and `\"` escapes the closing quote.
- UBT uses the Clang toolchain for this mode and needs LLVM installed. Warnings that the Clang or MSVC version is "newer than latest preferred" are harmless here.
- Entries point at `.rsp` response files under `Intermediate/`, which clangd expands automatically. The project must have been built at least once, or UBT generates what it needs.
- **What it covers:** an installed engine is precompiled, so the database lists only the project's and its plugins' translation units. clangd still indexes every engine header those files include, so engine types and declarations can be looked up. Engine `.cpp` bodies are not indexed. That's intentional, because a full-engine index would be many GB.

Verify on a project `.cpp` from the database:

```bash
"/c/Program Files/LLVM/bin/clangd.exe" --check="<PROJECT_DIR>/Source/<Module>/Private/<File>.cpp" 2>&1 | grep -E "All checks|E\["
```

Expect `All checks completed, 0 errors`. It takes about 10 s; the output is large, so filter it.

## 4. User-level clangd config for engine files

Create `%LOCALAPPDATA%\clangd\config.yaml` and **merge** into it if it already exists. Use forward slashes; `PathMatch` is a regex, so escape dots.

```yaml
# Engine source isn't in any compile_commands.json (installed engine builds are precompiled).
# Borrow the project's database so clangd infers include paths/defines when it opens engine files.
If:
  PathMatch: C:/Program Files/Epic Games/UE_5\.8/.*
CompileFlags:
  CompilationDatabase: C:/Users/alex/Documents/Unreal/CodeProject
# Borrowed flags lack each engine module's private include paths/API macros, so diagnostics there are noise.
Diagnostics:
  Suppress: "*"
```

- Without this, engine files opened through LSP find no database and fall back to bare flags.
- With it, clangd copies flags from the closest project file (the log shows `Compile command inferred from ...`). Navigation mostly works.
- Engine `.cpp` files still won't parse perfectly, because each module's `Private/` include paths and `*_API` export macros are missing. That's why diagnostics are turned off there.
- `clangd --check` on an engine file still reports an error count. That's expected; the individual errors are no longer reported.

## 5. graphify: index only the project's code (skip if the user doesn't use graphify)

A graph that includes the engine reaches 4–10 GB and takes minutes per query. To limit it:

1. Write `PROJECT_DIR\.graphifyignore` (gitignore syntax; a pattern without a leading `/` matches at any depth):

   ```gitignore
   # Only index our own code (Source/ and Plugins/). Engine source is searched with clangd, not graphify.

   # Build metadata and intermediates (at any depth, including inside plugins)
   Intermediate/
   DerivedDataCache/
   Saved/
   Binaries/
   .git/

   # Non-code project folders
   Content/
   Config/
   Build/
   .idea/
   .vs/
   .claude/
   .cache/
   graphify-out/
   graphify-out.*/
   ```

   `.cache/` is where clangd keeps its background index.

2. If an engine-wide `graphify-out/` exists (check `graphify-out/.graphify_root`, and the paths in `manifest.json`), **rename it** to `graphify-out.engine-old` rather than deleting it. Tell the user to delete it themselves.

3. Rebuild the graph. This is AST-only and uses no API key:

   ```bash
   graphify extract . --code-only --no-viz
   graphify cluster-only . --no-viz --no-label   # GRAPH_REPORT.md; --no-label skips LLM community naming
   ```

   Warnings about C++ files with "syntax errors" come from UE macros confusing tree-sitter, and are fine.

4. Replace any setup script that points `graphify extract` at the engine with the two commands above.

5. Check for a strict graphify integration left over from `graphify install --project --strict` / `graphify claude install`:
   - a `PreToolUse` hook running `graphify hook-guard` in `.claude/settings.json`;
   - a "MANDATORY: run graphify query first" section in `CLAUDE.md`.

   With a project-only graph, those rules steer agents away from engine search. Remove them with `graphify claude uninstall`, or edit them to match step 6, and **confirm with the user first**.

## 6. Agent guidance

Append to `PROJECT_DIR\.claude\CLAUDE.md` (or the project `CLAUDE.md`):

```markdown
# Code search
- C++ symbols (definitions, references, callers, overrides, types), in project or engine headers: use the LSP tool (clangd). Regenerate `compile_commands.json` with `clangd_setup.bat` after adding modules/files or changing Build.cs deps.
- Structure of our own code (Source/, Plugins/): `graphify query|path|explain`. The graph deliberately excludes the engine; refresh with `graphify update .` after code changes.
- Engine text search (engine is `<UE_ROOT>\Engine`, ~80k files): never grep the whole engine (times out). Narrow Grep to a module folder, e.g. `Engine/Source/Runtime/Renderer`.
```

## 7. Finish

Tell the user to:
- **Restart Claude Code**, so the PATH change and the plugin load. Then check that an LSP go-to-definition on a project symbol works.
- **Delete** any renamed `graphify-out.engine-old` and other leftovers themselves. Don't `rm -rf` large directories without explicit approval.

Do not:
- Suggest WSL- or Docker-based indexers (e.g. Embark's UnrealClaudeFileHelper). This user's WSL memory constraints make them unusable.
- Build a clangd or graphify index over the whole engine. That size problem is exactly what this setup avoids.
