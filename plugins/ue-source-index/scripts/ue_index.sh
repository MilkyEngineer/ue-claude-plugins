#!/usr/bin/env sh
# Finds a Python 3 (preferring one bundled with an Unreal Engine install) and runs ue_index.py.
# Works on macOS/Linux and in Git Bash on Windows (prefer this over ue_index.cmd from Bash: cmd.exe
# re-parses | ^ & in arguments). Override with the UE_INDEX_PYTHON environment variable.
case "$0" in */*) DIR="${0%/*}" ;; *) DIR="." ;; esac  # no subshell: this runs on every hooked tool call
CACHE="$HOME/.claude/ue-index/python.txt"
PY="${UE_INDEX_PYTHON:-}"
ok() { [ -n "$1" ] && "$1" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; }

# Fast path (this runs on every hooked tool call): trust an explicit or cached interpreter that exists
# instead of starting Python an extra time just to check its version.
if [ -z "$PY" ] || [ ! -f "$PY" ]; then
  PY=""
  [ -f "$CACHE" ] && PY="$(tr -d '\r' < "$CACHE")"
  [ -n "$PY" ] && [ ! -f "$PY" ] && PY=""
fi
if [ -z "$PY" ]; then
  for c in "/c/Program Files/Epic Games"/UE_*/Engine/Binaries/ThirdParty/Python3/Win64/python.exe \
           "/Users/Shared/Epic Games"/UE_*/Engine/Binaries/ThirdParty/Python3/Mac/bin/python3 \
           "$HOME"/UnrealEngine*/Engine/Binaries/ThirdParty/Python3/Linux/bin/python3 \
           "$(command -v python3 2>/dev/null)" "$(command -v python 2>/dev/null)"; do
    if ok "$c"; then PY="$c"; break; fi
  done
  if [ -z "$PY" ]; then echo "error: no Python 3.8+ found; set UE_INDEX_PYTHON" >&2; exit 1; fi
  mkdir -p "$(dirname "$CACHE")" && printf '%s\n' "$PY" > "$CACHE"
fi
# -S: skip site-packages setup (~25ms); the scripts use only the standard library.
exec "$PY" -S -X utf8 "$DIR/ue_index.py" "$@"
