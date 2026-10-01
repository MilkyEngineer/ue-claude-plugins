#!/bin/sh
# Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.
#
# SessionStart hook: in an Unreal project, checks that this plugin version's uak is installed in $UAK_HOME/<version>/
# (docs/INSTALL.md). If it isn't, tells the user, and gives Claude the publish command so it can help. Silent otherwise,
# and never blocks the session: it always exits 0.
#
# POSIX sh, so it runs wherever Claude Code runs hooks (Git Bash on Windows). It only reads files.

root="${CLAUDE_PLUGIN_ROOT:-$(dirname "$0")/..}"
project="${CLAUDE_PROJECT_DIR:-$PWD}"

# Only in an Unreal project: a .uproject here or above, or an engine source tree (a project may sit inside it).
is_unreal() {
	dir="$1"
	[ -f "$dir/Engine/Build/Build.version" ] && return 0
	while [ -n "$dir" ]; do
		for file in "$dir"/*.uproject; do
			[ -f "$file" ] && return 0
		done
		parent=$(dirname "$dir")
		[ "$parent" = "$dir" ] && break
		dir="$parent"
	done
	return 1
}
is_unreal "$project" || exit 0

version=$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$root/.claude-plugin/plugin.json" | head -n 1)
[ -n "$version" ] || exit 0

home="${UAK_HOME:-$HOME/.unreal-agent-kit}"
if [ -f "$home/$version/uak" ] || [ -f "$home/$version/uak.exe" ]; then
	exit 0
fi

# What is installed, if anything: an older version means the plugin was updated.
installed=$(ls "$home" 2>/dev/null | grep -E '^[0-9]+\.[0-9]+\.[0-9]+' | tr '\n' ' ' | sed 's/ $//')
if [ -n "$installed" ]; then
	status="uak $version is not installed (found: $installed). The unreal-agent-kit plugin was updated, so uak needs publishing again."
else
	status="uak $version is not installed yet."
fi

publish="<engine root>/Engine/Binaries/ThirdParty/DotNet/<version>/<platform>/dotnet publish \"$root/tools/src/uak\" -c Release"
context="$status Publish it once with the project's engine's bundled dotnet: $publish (the SDK folder is 10.0 in UE 5.8, 8.0.412 in UE 5.7; the platform is win-x64, linux-x64, mac-arm64 and so on). It installs to $home/$version/. Then make sure that uak is the one on PATH, or the full path the project's CLAUDE.md names. Steps: $root/docs/INSTALL.md. Until then, uak commands fail or run an older uak; offer to run the publish for the user."

# JSON strings: escape backslashes and quotes.
json() {
	printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

printf '{"systemMessage":"%s","hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"%s"}}\n' \
	"$(json "unreal-agent-kit: $status See docs/INSTALL.md, or ask Claude to publish it.")" "$(json "$context")"
exit 0
