#!/usr/bin/env sh
# Finds a Python 3 (preferring one bundled with an Unreal Engine install) and runs ue_index.py.
# Works on macOS/Linux and in Git Bash on Windows (prefer this over ue_index.cmd from Bash: cmd.exe
# re-parses | ^ & in arguments). Override with the UE_INDEX_PYTHON environment variable.
DIR="$(cd "$(dirname "$0")" && pwd)"
CACHE="$HOME/.claude/ue-index/python.txt"
PY="${UE_INDEX_PYTHON:-}"
ok() { [ -n "$1" ] && "$1" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; }

if ! ok "$PY" && [ -f "$CACHE" ]; then PY="$(tr -d '\r' < "$CACHE" | sed 's/[[:space:]]*$//')"; fi
if ! ok "$PY"; then
  PY=""
  for c in "/c/Program Files/Epic Games"/UE_*/Engine/Binaries/ThirdParty/Python3/Win64/python.exe \
           "/Users/Shared/Epic Games"/UE_*/Engine/Binaries/ThirdParty/Python3/Mac/bin/python3 \
           "$HOME"/UnrealEngine*/Engine/Binaries/ThirdParty/Python3/Linux/bin/python3 \
           "$(command -v python3 2>/dev/null)" "$(command -v python 2>/dev/null)"; do
    if ok "$c"; then PY="$c"; break; fi
  done
  if [ -z "$PY" ]; then echo "error: no Python 3.8+ found; set UE_INDEX_PYTHON" >&2; exit 1; fi
  mkdir -p "$(dirname "$CACHE")" && printf '%s\n' "$PY" > "$CACHE"
fi
exec "$PY" -X utf8 "$DIR/ue_index.py" "$@"
