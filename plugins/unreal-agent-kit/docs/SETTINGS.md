# Recommended Claude Code settings

These settings make long multi-agent sessions sturdier. A plugin cannot set them for you (see the last section), so add them yourself.

Where they go:
- `~/.claude/settings.json`: your user settings, for every project;
- `<project>/.claude/settings.json`: shared with the team, checked in;
- `<project>/.claude/settings.local.json`: yours only, not checked in.

Environment variables go under `"env"` in any of these. Claude Code reads them at start-up, so they survive restarts and need no shell profile.

## Keep background shells alive

```json
{
  "env": {
    "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP": "1"
  }
}
```

Under memory pressure, Claude Code may end background shells early. A build or test run then dies half-way, and an agent waits on a result that never comes. This variable turns that off.

It does not stop the two-hour limit on background shells, or their death when Claude Code restarts. For those, start runs with `uak runs start`.

## Auto-compaction

Long lead sessions fill their context. Compaction summarises the older conversation. Compacting earlier leaves more room for the summary and for the next task, at the cost of detail:

```json
{
  "autoCompactEnabled": true,
  "autoCompactWindow": 600000,
  "env": {
    "CLAUDE_AUTOCOMPACT_PCT_OVERRIDE": "70"
  }
}
```

- `autoCompactWindow` is the context size, in tokens, that compaction measures against. With a large-context model, a smaller window compacts sooner and keeps sessions quicker and cheaper.
- `CLAUDE_AUTOCOMPACT_PCT_OVERRIDE` is the percentage of that window at which compaction starts.

The values above are a starting point, not a rule: tune them to your model and budget. Whatever you choose, keep decisions and state in files (plans, briefs, review records), because a summary loses detail.

## Usage limits

```json
{
  "autoContinueAtUsageLimit": true
}
```

This lets Claude Code carry on once a usage limit resets. It does not always wake a session whose agents all stopped. The workflow skill can also keep an hourly watchdog (CronCreate) that resumes them, but only if you allow auto-resume: it is off by default. To allow it, tell the lead, or record it in the project's CLAUDE.md, for example "Auto-resume: allowed. The lead may keep a usage-limit watchdog and resume agents without asking." Leave both off if you would rather decide yourself when work resumes.

## Notifications

`"agentPushNotifEnabled": true` lets the lead send you a push notification, for example for a critical question during an unattended milestone.

## Per-project `uak` settings

`UAK_PROJECT`, `UAK_ENGINE`, `UAK_VCS`, `UAK_MAX_PARALLEL_ACTIONS`, `UAK_P4_TIMEOUT`, `UAK_GIT_TIMEOUT`, `UAK_PROJECT_COMMANDS` and `UAK_COMMAND_PATHS` fit well in the project's `.claude/settings.local.json`:

```json
{
  "env": {
    "CLAUDE_CODE_DISABLE_BG_SHELL_PRESSURE_REAP": "1",
    "UAK_MAX_PARALLEL_ACTIONS": "3"
  }
}
```

See [INSTALL.md](INSTALL.md) for what each one means. `UAK_COMMAND_PATHS` takes absolute folders only; `uak` skips a relative entry with a warning.

`UAK_PROJECT_COMMANDS=1` makes `uak` load the command DLLs in `<Project>/.uak/commands/`, which then run with your rights. Set it only in `settings.local.json`, and only for a project whose `.uak/commands/` you trust: a checked-in `settings.json` would turn it on for everyone who clones the project.

Leave `UAK_LOCK_NAME` and `UAK_LOCK_PRIORITY` unset here: `uak runs start` sets them for each run, and `-priority=` sets the priority per call.

## What a plugin cannot set

A plugin ships agents, skills, hooks and similar content. It cannot change your settings, so it cannot:
- set environment variables for your sessions (including the ones above, and `PATH` for `uak`);
- turn on auto-compaction, change its window, or turn on `autoContinueAtUsageLimit`;
- grant permissions, or allow commands without prompting;
- create a scheduled watchdog: the lead creates it in each session;
- publish `uak`, or edit your project's CLAUDE.md.

This is deliberate: installing a plugin should not quietly change how Claude Code behaves elsewhere.
