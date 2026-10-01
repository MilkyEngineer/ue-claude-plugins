# CLAUDE.md snippet

Paste the block below into your project's CLAUDE.md, then replace each `<...>`. The agents' ground rules point at CLAUDE.md for anything project-specific, so fill in every line that applies and delete the rest.

````markdown
# Multi-agent workflow (UnrealAgentKit)

- **Agents:** use the `unreal-agent-workflow` skill to plan and run work with the `ue-low`, `ue-medium`, `ue-high`, `ue-xhigh` and `ue-review` agents. Caps on agents at once: <4> low, <2> medium, <2> high, <1> xhigh, <1> review.
- **Where things are:**
  - code: <Source/..., Plugins/...>;
  - spec: <Documentation/Spec>, mirrored in <a separate doc, or "not mirrored">;
  - plans and briefs: <Documentation/Plans/<milestone>-Plan.md and Documentation/Plans/<milestone>/>;
  - review records: <Documentation/Reviews>.
- **`uak`:** <on PATH | run as `~/.unreal-agent-kit/<version>/uak`>. If it is missing, publish the kit first (the plugin's docs/INSTALL.md). `uak env` shows what it resolved; `uak help <command>` gives the options.
- **Builds and tests:** `uak build`; `uak test -filter=<Project>.<Area> -name=<unique>`; add `-gpu` for tests that need a GPU. Both take the editor lock themselves. Other editor runs: `uak lock run -name=<unique> -- <command...>`. Compile checks without the lock: `uak compile <file>... [-dependents]`. Parallelism cap: <UAK_MAX_PARALLEL_ACTIONS=3, because the machine has 16 GB RAM>.
- **Long runs:** `uak runs start -name=<unique> -owner=<W# or lead> -- <command...>`; follow with `uak runs list`.
- **Project tools:** <none | `uak` commands in `<Project>/.uak/commands/`, turned on with `UAK_PROJECT_COMMANDS=1` in `.claude/settings.local.json`: name them>.
- **Tests:** automation tests live in <Source/<Project>Tests>, named <Project>.<Area>.<Name>.
- **Style:** <file header text>, <tabs or spaces>, Unreal naming, and the comment density of the surrounding code. <Line endings.>
- **Spec style:** <British English, short sentences, section links as "[section N](file.md)">.
- **Correctness:** the worst failure here is <for example "a silent wrong result" or "data loss">. When unsure, choose the conservative option. Changes to generated or cached output bump <the version constants>.
- **Code search:** <clangd for C++ symbols; an engine source index; grep one engine module folder at a time>.
- **Version control:** <git or Perforce>. Agents never commit or push unless told. <Milestone loop: "on a passing verification, commit and continue to the next milestone" | "stop at each milestone and report">.
- **Usage limits:** <"Auto-resume: allowed" (the lead keeps a watchdog and resumes agents after a usage limit resets) | "ask first" (the default)>.
````
