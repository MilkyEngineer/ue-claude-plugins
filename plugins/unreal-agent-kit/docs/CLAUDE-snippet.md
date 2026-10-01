# CLAUDE.md snippet

Paste the block below into your project's CLAUDE.md, then replace each `<...>`. The agents' ground rules point at CLAUDE.md for anything project-specific, so fill in every line that applies and delete the rest.

````markdown
# Multi-agent workflow (UnrealAgentKit)

- **Agents:** use the `unreal-agent-workflow` skill to plan and run work with the `runner`, `low`, `medium`, `high`, `xhigh`, `research`, `review` and `architect` agents (shown as `unreal-agent-kit:<name>`). Caps on agents at once: <4> runner, <4> low, <2> medium, <2> high, <1> xhigh, <2> research, <1> review, <1> architect.
- **Where things are:**
  - code: <Source/..., Plugins/...>;
  - spec: <Documentation/Spec>, mirrored in <a separate doc, or "not mirrored">;
  - plans and briefs: <Documentation/Plans/<milestone>-Plan.md and Documentation/Plans/<milestone>/>;
  - review records: <Documentation/Reviews>.
- **`uak`:** the plugin puts it on PATH in Claude Code sessions. If it says it isn't published, publish the kit first (the plugin's docs/INSTALL.md). `uak env` shows what it resolved; `uak help <command>` gives the options.
- **Builds and tests:** `uak build`; `uak test -filter=<Project>.<Area> -name=<unique>`; add `-gpu` for tests that need a GPU, or `-windowed` for tests that need a real viewport and Slate windows. Both take the editor lock themselves. More UBT or editor arguments go after `--`: <`uak build -- -DisableAdaptiveUnity`, `uak test ... -- -SCCProvider=None`>. Other editor runs: `uak lock run -name=<unique> -- <command...>`. Compile checks without the lock: `uak compile <file>... [-dependents]`. Parallelism cap: <UAK_MAX_PARALLEL_ACTIONS=3, because the machine has 16 GB RAM>.
- **Long runs:** anything that may run over an hour: `uak runs start -name=<unique> -owner=<E# or lead> -- <command...>`. Wait for it with `uak runs wait -name=<name> -timeout=<seconds>` in a background shell whose own time limit is longer than `-timeout` and within the shell's two-hour limit; on exit 3 (still running), start another wait. `uak runs list` shows what is running.
- **Project tools:** <none | `uak` commands in `<Project>/.uak/commands/`, turned on with `UAK_PROJECT_COMMANDS=1` in `.claude/settings.local.json`: name them>.
- **Tests:** automation tests live in <Source/<Project>Tests>, named <Project>.<Area>.<Name>.
- **Style:** <file header text>, <tabs or spaces>, Unreal naming, and the comment density of the surrounding code. <Line endings.>
- **Spec style:** <British English, short sentences, section links as "[section N](file.md)">.
- **Correctness:** the worst failure here is <for example "a silent wrong result" or "data loss">. When unsure, choose the conservative option. Changes to generated or cached output bump <the version constants>.
- **Code search:** <clangd for C++ symbols; an engine source index; grep one engine module folder at a time>.
- **Version control:** <git or Perforce>. Agents never commit or push unless told. <Perforce: `uak vcs edit|add|reopen|shelve|change` for this client's pending changelists; never revert or submit.>
- **Horde:** <none | preflights with `uak horde preflight -c=<shelved CL> [-wait -timeout=<s>]`; wait for a job with `uak horde job -id=<job> -wait -timeout=<s>` in a background shell. The server is set once with `uak horde config -server=<url>`. A horde command may open a sign-in page on the user's desktop, so agents tell the lead first, and send the lead the "Job URL: ..." line at once. `-shelve` only on changelists the agent created. On exit 6 (build settings needed) the lead asks the user (AskUserQuestion) and saves the answers with `uak horde config`. `-autosubmit` only when the user asks.> <Milestone loop: "on a passing verification, commit and continue to the next milestone" | "stop at each milestone and report">.
- **Usage limits:** <"Auto-resume: allowed" (the lead keeps a watchdog and resumes agents after a usage limit resets) | "ask first" (the default)>.
````
