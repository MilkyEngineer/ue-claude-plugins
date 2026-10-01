# ue-claude-plugins

Claude Code plugins for Unreal Engine development.

## Install

```
/plugin marketplace add MilkyEngineer/ue-claude-plugins
/plugin install unreal-agent-kit@ue-claude-plugins
```

Or from a shell: `claude plugin marketplace add MilkyEngineer/ue-claude-plugins`, then `claude plugin install <plugin>@ue-claude-plugins`.

## Plugins

### unreal-agent-kit

A multi-agent workflow for Unreal Engine projects, and `uak`, the CLI it runs on. One lead plans a milestone, splits it into epics and delegates each to a worker at the right effort. Workers share one editor, so builds and editor runs queue on a lock.

- **Agents:** `low` (Sonnet), `medium`, `high` and `xhigh` workers, none of which can spawn agents; `runner`, which runs and reports but cannot edit; two read-only agents, `research` for engine investigations and `review` for adversarial reviews; and `architect`, which drafts milestone plans and briefs. Claude Code shows them as `unreal-agent-kit:<name>`.
- **Workflow skill:** effort tiers and caps, milestone plans with epic briefs (templates included), delegation, resume-or-respawn, reviews, spec-doc sync, and an opt-in usage-limit and restart watchdog.
- **`uak`:** a queued editor lock that is released when its holder dies; detached runs that survive shells and restarts; single-file compile checks through UBT; build and automation-test wrappers with count checks; Git and Perforce status.

**Requirements:** an Unreal Engine install (UE 5.8 or 5.7). `uak` is published once, as a self-contained program, with the engine's bundled .NET SDK: see [INSTALL.md](plugins/unreal-agent-kit/docs/INSTALL.md). Windows is tested; the Linux and Mac code is untested.

## License

MIT
