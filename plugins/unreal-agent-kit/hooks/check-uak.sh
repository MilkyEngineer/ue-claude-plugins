#!/bin/sh
# Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.
#
# SessionStart hook: in an Unreal project, checks that this plugin version's uak is installed in $UAK_HOME/<version>/
# (docs/INSTALL.md). If it isn't, tells the user, and tells Claude to ask them, before their first request, whether to
# publish it now, with the exact command for the project's engine when it can find that engine. A hook can't start a turn
# itself, so the question comes with Claude's first reply. Silent otherwise, and never blocks the session: it always
# exits 0.
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

# The engine to publish with, found as uak finds it (docs/INSTALL.md, step 7) as far as sh can: UAK_ENGINE, which the build
# also reads first; else the project's EngineAssociation: a path, relative to the project's folder; empty, the engine that
# contains the project; a version, the launcher's default install folder. A registered build's GUID needs the registry, so
# it is not found here.
uproject=
find_uproject() {
	dir="$1"
	depth=0
	while [ -n "$dir" ] && [ "$depth" -lt 40 ]; do
		case "$dir" in
			/|//) return 1 ;;
			//*/*) ;;
			//*) return 1 ;;
		esac
		for file in "$dir"/*.uproject; do
			if [ -f "$file" ]; then
				uproject="$file"
				return 0
			fi
		done
		parent=$(dirname "$dir")
		[ "$parent" = "$dir" ] && return 1
		dir="$parent"
		depth=$((depth + 1))
	done
	return 1
}

# The engine root at or above a folder: the one holding Engine/Build/Build.version.
containing_engine() {
	dir="$1"
	depth=0
	while [ -n "$dir" ] && [ "$depth" -lt 40 ]; do
		case "$dir" in
			/|//) return 1 ;;
			//*/*) ;;
			//*) return 1 ;;
		esac
		if [ -f "$dir/Engine/Build/Build.version" ]; then
			printf '%s\n' "$dir"
			return 0
		fi
		parent=$(dirname "$dir")
		[ "$parent" = "$dir" ] && return 1
		dir="$parent"
		depth=$((depth + 1))
	done
	return 1
}

# An engine root, given the root or its Engine folder.
engine_at() {
	if [ -f "$1/Engine/Build/Build.version" ]; then
		printf '%s\n' "$1"
	elif [ -f "$1/Build/Build.version" ]; then
		dirname "$1"
	else
		return 1
	fi
}

engine_root() {
	if [ -n "$UAK_ENGINE" ]; then
		engine_at "$(posix_path "$UAK_ENGINE")"
		return
	fi
	find_uproject "$project" || { containing_engine "$project"; return; }
	project_dir=$(dirname "$uproject")
	association=$(sed -n 's/^.*"EngineAssociation"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$uproject" | head -n 1)
	case "$association" in
		"")
			containing_engine "$project_dir"
			;;
		*/*|*\\*)
			path=$(printf '%s' "$association" | sed 's/\\/\//g')
			case "$path" in
				/*|[A-Za-z]:*) path=$(posix_path "$path") ;;
				*) path="$project_dir/$path" ;;
			esac
			engine_at "$path"
			;;
		[0-9]*.[0-9]*)
			if [ -n "$windows" ]; then
				programs=$(posix_path "${ProgramFiles:-C:\\Program Files}")
				engine_at "$programs/Epic Games/UE_$association"
			elif [ "$(uname -s 2>/dev/null)" = "Darwin" ]; then
				engine_at "/Users/Shared/Epic Games/UE_$association"
			else
				return 1
			fi
			;;
		*)
			return 1
			;;
	esac
}

# The engine's bundled dotnet for this host: Engine/Binaries/ThirdParty/DotNet/<highest version with this platform's SDK>/<rid>/.
bundled_dotnet() {
	base="$1/Engine/Binaries/ThirdParty/DotNet"
	[ -d "$base" ] || return 1
	if [ -n "$windows" ]; then
		os=win
		program=dotnet.exe
		case "${PROCESSOR_ARCHITEW6432:-$PROCESSOR_ARCHITECTURE}" in
			ARM64) arch=arm64 ;;
			*) arch=x64 ;;
		esac
	else
		program=dotnet
		case "$(uname -s 2>/dev/null)" in
			Darwin) os=mac ;;
			*) os=linux ;;
		esac
		case "$(uname -m 2>/dev/null)" in
			arm64|aarch64) arch=arm64 ;;
			*) arch=x64 ;;
		esac
	fi
	rid="$os-$arch"
	best=$(ls "$base" 2>/dev/null | grep -E '^[0-9]+(\.[0-9]+)*$' | sort -t. -k1,1n -k2,2n -k3,3n | while read -r sdk; do
		[ -f "$base/$sdk/$rid/$program" ] && printf '%s\n' "$sdk"
	done | tail -n 1)
	[ -n "$best" ] || return 1
	printf '%s\n' "$base/$best/$rid/$program"
}

dotnet=
engine=$(engine_root 2>/dev/null) && dotnet=$(bundled_dotnet "$engine")

# Older installs: removed once the new one is published. Only the version folders listed, never State/ (the fallback state
# folder, with locks and run records). A detached run started from an old uak keeps its folder's files open, so removing that
# folder fails until the run ends: then it is left for next time.
cleanup=
if [ -n "$installed" ]; then
	folders=
	for old in $installed; do
		folders="$folders \"$home/$old\""
	done
	cleanup=" After a successful publish, uninstall the older versions ($installed) with your Bash tool: rm -rf$folders. If a folder can't be removed, a detached run is still using it (uak runs list): leave it and say so. Point PATH, or the project's CLAUDE.md, at $home/$version/ instead of an older folder."
fi
if [ -n "$installed" ]; then
	publish_option="Publish now and remove the old versions (Recommended)"
else
	publish_option="Publish now (Recommended)"
fi
ask="Before you act on the user's first message, ask them with the AskUserQuestion tool whether to publish uak $version now, with the options \"$publish_option\" and \"Not now\". If they choose not now, say once that uak commands fail or run an older uak until it is published, then carry on with their request."
after="It installs to $home/$version/. Afterwards, uak must be the one on PATH, or the full path the project's CLAUDE.md names (steps: $root/docs/INSTALL.md).$cleanup"
if [ -n "$dotnet" ]; then
	publish="DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false \"$dotnet\" publish \"$root/tools/src/uak\" -c Release"
	context="$status $ask If they choose to publish, run this sh command with your Bash tool, never PowerShell or cmd, which can't read its VAR=value prefix or its /c/-style paths (the project's engine, $engine, with its bundled dotnet; in the background if your shell tool allows), and report the result: $publish. It takes about a minute, and the first publish downloads the .NET runtime pack from NuGet once. $after"
else
	publish="<engine root>/Engine/Binaries/ThirdParty/DotNet/<version>/<platform>/dotnet publish \"$root/tools/src/uak\" -c Release"
	context="$status $ask If they choose to publish, find the project's engine first (this hook couldn't: a registered build's GUID, or no bundled SDK), then run its bundled dotnet: $publish (the SDK folder is 10.0 in UE 5.8, 8.0.412 in UE 5.7; the platform is win-x64, linux-x64, mac-arm64 and so on), with DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false set. $after"
fi

# JSON strings: escape backslashes and quotes.
json() {
	printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

printf '{"systemMessage":"%s","hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"%s"}}\n' \
	"$(json "unreal-agent-kit: $status Claude will offer to publish it with your first message (docs/INSTALL.md has the steps).")" "$(json "$context")"
exit 0
