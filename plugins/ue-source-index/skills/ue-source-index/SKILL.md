---
name: ue-source-index
description: Pre-built, greppable index of Unreal Engine C++ source (types, UFUNCTIONs, UPROPERTYs, exported functions, delegates, CVars, log categories, config/ini keys, deprecations, module dependencies, and a notes file of hard-won findings). Use it BEFORE grepping, globbing or browsing Unreal Engine source (Engine/Source, Engine/Plugins), e.g. to find where a UE class, function, struct, enum, delegate, CVar, log category or ini setting is defined, which header to #include or which module to add to Build.cs, what's deprecated, or how an engine system works. Also use it when working in an Unreal project (.uproject, *.Build.cs) and you need engine internals.
---

# Unreal Engine source index

Engine source is huge (~120k files), so grepping it directly is slow and floods context. Instead, this skill keeps a compact index per engine version. The index answers "where is X / what includes X / what is X called" with one small grep. Use it first, then open real source only at the `file:line` it gives you.

`UEI` below means the launcher script in `${CLAUDE_PLUGIN_ROOT}/scripts/`. Keep the path quoted, because on Windows it contains backslashes:
- **Bash** (including Git Bash on Windows): `sh "${CLAUDE_PLUGIN_ROOT}/scripts/ue_index.sh" ...`
- **PowerShell / cmd**: `& "${CLAUDE_PLUGIN_ROOT}/scripts/ue_index.cmd" ...`. cmd.exe re-parses `| ^ & < >` inside arguments, so for regex terms use the Bash launcher or grep the .tsv instead.

Both find a Python 3 themselves. They prefer the one bundled with the engine, so nothing needs installing.

## 1. Check the index (always first)

```
UEI status                      # auto-detects the engine from cwd (.uproject or engine tree)
UEI status --project path/to/Game.uproject
UEI status --engine 5.7         # or an engine root path, or a source-build GUID
```

`status` prints `INDEX <dir>` and one of these states:

| State (exit code) | What to do |
|---|---|
| `fresh` (0) | Go to step 2. |
| `missing` (2) / `stale` (3) | Build it. Start `UEI build --engine "<root>"` **in the background** (Bash/PowerShell `run_in_background`) and tell the user it's building. A full build of an installed 5.8 takes ~5–6 min on an SSD (~80k files; the directory scan alone is 1–2 min cold), and several times longer on an HDD (add `--jobs 2` there). Source builds rebuild incrementally, so only changed modules get re-parsed. While it runs, keep engine searches narrow (one folder or file). |
| `building` (4) | Another session is building it. Work narrowly, and re-check `status` later. |

Where indexes live:
- **Installed** engines (have `Engine/Build/InstalledBuild.txt`): `~/.claude/ue-index/<ver>-CL<changelist>/`. Shared across all repos. Rebuilt only when `Build.version`, the Marketplace plugin set, or the index schema changes.
- **Source** builds: `<root>/Engine/.claude/ue-index/`. Stale when git `HEAD` changes. Local uncommitted edits aren't detected by `status`, so run `build` again after changing engine code. It only reparses changed modules.

If engine detection fails, `status` lists known engines. Pass `--engine` or `--project` explicitly. A folder full of projects is ambiguous.

## 2. Query, cheapest first

Paths below are relative to the INDEX dir from `status`. File paths inside the index are relative to the engine root.

1. **`UEI find <name>`** gives ranked matches across every table. For types it also prints the `#include` line and the Build.cs module. Options: `--exact`, `--kind symbols,functions,cvars,logs,config,deprecated`, `--regex`, `--limit N`.
2. **Grep one .tsv file** with the Grep tool. Set `path` to that file, never the engine tree. **Never Read a .tsv whole**; they are tens of MB.

| File | Columns (tab-separated) | Grep patterns |
|---|---|---|
| `symbols.tsv` | name, kind, module, file:line, detail | `^AActor\t` type · `^AActor::` its UFUNCTIONs/UPROPERTYs/nested types · `::BeginPlay\t` member by name · `\tuclass\t.*Blueprintable` |
| `functions.tsv` | name, module, file:line, signature | `^UWorld::SpawnActor` · `::GetGameInstance\t` (exported non-UFUNCTION functions, free and member) |
| `cvars.tsv` | name, kind, module, file:line, type, flags, help | `^r\.Lumen\.` · `(?i)^[^\t]*shadow[^\t]*\tvar` |
| `logs.tsv` | name, kind, module, file:line, verbosity | `^LogNet\t` |
| `config.tsv` | section, key, source(ini/code/uprop), module, file:line, detail | `\tbSmoothFrameRate\t` · `^/Script/Engine\.Engine\t` · `^\[?/Script/EnhancedInput` |
| `deprecated.tsv` | name, since, module, file:line, message | `^UGameplayStatics::` · `\t5\.[0-9]\t` |
| `modules.tsv` | name, type, plugin, path, doc, public_deps, private_deps, purpose | `^GameplayAbilities\t` · `(?i)\tinventory` for purpose search |

   symbols.tsv kinds: `uclass ustruct uenum uiface` (reflected) · `class struct enum union` (header types) · `cpp-class cpp-struct` (defined in a .cpp) · `ufunc uprop delegate alias gtag`. The `detail` column holds bases plus UCLASS/UFUNCTION/UPROPERTY specifiers and signatures, so you often don't need to open the header at all.
3. **`UEI module <Name> [--dependents]`** prints a module's path, dependencies, purpose, and its doc file. `modules/<Name>.md` has deps, stats, and a folder table of contents with line numbers. Read the TOC, then Read with `offset` for one folder. Don't read large module docs whole (Engine's is thousands of lines).
4. **`INDEX.md`** is small, so it's fine to Read once per session. It covers the file table, a map of core modules (what lives in Core / CoreUObject / Engine / RenderCore / Slate...), and areas.
5. **Only then open source.** Read the header or .cpp at the given line with a small `offset`/`limit` window. If you still need to search, grep **one file or one module folder** that the index pointed at, never `Engine/` as a whole.

Include rule: include path = header path after `Public/`, `Classes/` or `Internal/`. Module = Build.cs dependency. `Private/` headers aren't includable from other modules.

Not indexed: methods that are neither UFUNCTION nor `*_API`-exported (find the class, then grep its header), `#define` macros, ThirdParty, shaders (`Engine/Shaders/*.usf|ush`), C# (UBT/UAT), content, and your project's own source. Parsing is heuristic (regex over UE conventions), so treat a missing row as "probably not there", not proof.

## 3. NOTES.md: check before digging, add after hard-won findings

`<INDEX>/NOTES.md` holds one line per finding: `nNNN [Module] Symbol - finding @file:line#hash`.

- **Read:** grep it for `\[Module\]` or a symbol before a deep investigation. **Never Read it whole.** A `[STALE?]` marker means the anchored code changed since the note was written, so re-verify it.
- **Write** only through the CLI; it enforces the limits:
  ```
  UEI note add --module GameplayAbilities --symbol UAbilitySystemComponent::TryActivateAbility \
      --anchor Engine/Plugins/Runtime/GameplayAbilities/Source/GameplayAbilities/Private/AbilitySystemComponent_Abilities.cpp:512 \
      --text "Activation silently fails when NetExecutionPolicy is ServerOnly and called on client; check CanActivateAbility's tag branch"
  UEI note replace n007 --module ... --anchor ... --text "..."
  UEI note remove n007 | UEI note list --module X | UEI note check
  ```
- **Only add a note** when finding the answer took real digging (several searches or reads), or when the obvious answer was wrong. Don't add notes for things the index already answers ("X is defined in Y"). Point at code; don't explain it.
- Limits: ≤280 chars, a valid module name, and an anchor inside the engine root. Near-duplicates are rejected, and each module is capped at 15 notes. If the cap rejects a note, replace the weakest note in that module instead.
- Avoid double quotes inside `--text` (Windows argument quoting).
- On rebuild, notes are re-anchored by content hash. Moved code gets the line updated, changed code gets `[STALE?]`, and notes whose file was deleted are dropped. A new engine version starts with the previous version's notes whose anchors still match.

## Maintenance

- `UEI engines` lists the engines the launcher and registry know about.
- `UEI build --full` ignores the per-module cache. `--force` clears a lock left by a crashed build (locks older than 3h are ignored automatically).
- `build-errors.txt` in the index dir lists files that failed to parse (rare).
- The plugin's PreToolUse hook reminds you about this skill once per session per engine, when a Grep/Glob/Bash/PowerShell search targets an engine directory.
