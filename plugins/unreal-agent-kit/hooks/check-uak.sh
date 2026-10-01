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

# On Windows (Git Bash, MSYS or Cygwin), paths may come in Windows form (C:\...). The POSIX form (/c/...) walks up cleanly to
# "/", where dirname stops; the Windows form would stop at "C:".
windows=
case "$(uname -s 2>/dev/null)" in
	MINGW*|MSYS*|CYGWIN*) windows=1 ;;
esac
posix_path() {
	if [ -n "$windows" ] && command -v cygpath >/dev/null 2>&1; then
		cygpath -u "$1" 2>/dev/null || printf '%s\n' "$1"
	else
		printf '%s\n' "$1"
	fi
}
root=$(posix_path "$root")
project=$(posix_path "$project")

# Only in an Unreal project: a .uproject here or above, or an engine source tree (a project may sit inside it).
# The walk stops before the filesystem root and after 40 levels: in Git Bash, "/" globs as "//*", which browses the network
# for UNC hosts and takes seconds. A UNC host ("//server") is never searched either.
is_unreal() {
	dir="$1"
	[ -f "$dir/Engine/Build/Build.version" ] && return 0
	depth=0
	while [ -n "$dir" ] && [ "$depth" -lt 40 ]; do
		case "$dir" in
			/|//) break ;;
			//*/*) ;;
			//*) break ;;
		esac
		for file in "$dir"/*.uproject; do
			[ -f "$file" ] && return 0
		done
		parent=$(dirname "$dir")
		[ "$parent" = "$dir" ] && break
		dir="$parent"
		depth=$((depth + 1))
	done
	return 1
}
is_unreal "$project" || exit 0

version=$(sed -n 's/^.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$root/.claude-plugin/plugin.json" | head -n 1)
[ -n "$version" ] || exit 0

# The user's folder as uak itself finds it: on Windows that is USERPROFILE, which Git Bash's HOME need not match.
user_home="$HOME"
if [ -n "$windows" ] && [ -n "$USERPROFILE" ]; then
	user_home=$(posix_path "$USERPROFILE")
fi
if [ -n "$UAK_HOME" ]; then
	home=$(posix_path "$UAK_HOME")
else
	home="$user_home/.unreal-agent-kit"
fi
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
