# ue-claude-plugins

Claude Code plugins for Unreal Engine development.

## Install

```
/plugin marketplace add MilkyEngineer/ue-claude-plugins
/plugin install ue-source-index@ue-claude-plugins
/plugin install unreal-agent-kit@ue-claude-plugins
```

Or from a shell: `claude plugin marketplace add MilkyEngineer/ue-claude-plugins`, then `claude plugin install <plugin>@ue-claude-plugins`.

## Plugins

### ue-source-index

Engine source is ~120k files, and grepping it directly is slow and burns context. This plugin builds a compact, greppable index per engine version and teaches agents to use it first.

- **What's indexed:** types (with UCLASS/USTRUCT specifiers and bases), UFUNCTIONs and UPROPERTYs (with signatures and specifiers), `*_API` exported functions, delegates, CVars and console commands (with help text), log categories, config keys (ini defaults, `GConfig` reads, `UPROPERTY(config)`), deprecations, and module dependencies. Each module also gets a summary page.
- **`find` command:** answers "where is X / what do I `#include` / which Build.cs module" in one call.
- **`NOTES.md`:** short findings anchored to `file:line`. Agents grep it rather than read it. Size caps and dedupe are enforced by the CLI. Notes are re-anchored by content hash when the engine changes: moved code gets the line updated, changed code gets flagged, and notes on deleted files are removed.
- **Where indexes live:**
  - Installed engines (`Engine/Build/InstalledBuild.txt` present): `~/.claude/ue-index/<version>-CL<changelist>/`, shared by every repo. Rebuilt only when `Build.version` changes.
  - Source builds: `<root>/Engine/.claude/ue-index/` (git-ignored). Marked stale when git `HEAD` moves, and rebuilt incrementally per module.
- **Reminder hook:** a `PreToolUse` hook reminds the agent about the index (or tells it to build one) the first time in a session that it searches inside an engine directory.

**Requirements:** Python 3.8+. The Python bundled with every UE5 install (`Engine/Binaries/ThirdParty/Python3`) is found automatically; `UE_INDEX_PYTHON` overrides it. The hook runs through `sh`, which on Windows means Git Bash (Claude Code's default hook shell there).

**Build time:** roughly 1–6 minutes per engine, depending on disk speed. A no-change rebuild of a source build takes ~10 seconds.

### unreal-agent-kit

A multi-agent workflow for Unreal Engine projects, and `uak`, the CLI it runs on. One lead plans a milestone, splits it into workstreams and delegates each to a worker at the right effort. Workers share one editor, so builds and editor runs queue on a lock.

- **Agents:** `ue-low` (Sonnet), `ue-medium`, `ue-high` and `ue-xhigh` workers, none of which can spawn agents, and `ue-review`, a read-only adversarial reviewer.
- **Workflow skill:** effort tiers and caps, milestone plans with workstream briefs (templates included), delegation, reviews, spec-doc sync, and an opt-in usage-limit and restart watchdog.
- **`uak`:** a queued editor lock that is released when its holder dies; detached runs that survive shells and restarts; single-file compile checks through UBT; build and automation-test wrappers with count checks; Git and Perforce status.

**Requirements:** an Unreal Engine install (UE 5.8). `uak` is published once, as a self-contained program, with the engine's bundled .NET SDK: see [INSTALL.md](plugins/unreal-agent-kit/docs/INSTALL.md). Windows is tested; the Linux and Mac code is untested.

## License

MIT
