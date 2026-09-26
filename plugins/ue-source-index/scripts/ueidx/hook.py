"""PreToolUse hook: when an agent is about to search engine source, point it at the index (once per session per engine).

Never blocks and never fails loudly; any problem just means no reminder.

This runs before every Grep/Glob/Bash/PowerShell call, so imports are deferred until the cheap
substring pre-filter in run() says the call might be a search."""
import os
import sys

# Plain substrings, checked before anything heavier is imported. Grep/Glob calls always pass via the tool name.
_MAYBE_SEARCH = ("grep", "glob", "rg ", "ag ", "findstr", "select-string", "sls ", "find", "get-childitem", "gci",
                 "dir", "ls", "fd ")
_SEARCH_CMD = r"\b(grep|egrep|rg|ag|findstr|select-string|sls|find|get-childitem|gci|dir|ls|fd|git\s+grep)\b"
SCRIPTS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def _wrapper(tool):
    """Launcher spelled for the shell the agent is likely to use next (the .cmd mangles | ^ & in args)."""
    if os.name == "nt" and tool == "PowerShell":
        return '& "%s"' % os.path.join(SCRIPTS, "ue_index.cmd")
    return 'sh "%s"' % os.path.join(SCRIPTS, "ue_index.sh").replace("\\", "/")


def _targets(data):
    import re
    tool = data.get("tool_name") or ""
    ti = data.get("tool_input") or {}
    cwd = data.get("cwd") or os.getcwd()
    if tool in ("Grep", "Glob"):
        p = ti.get("path") or ""
        if p and not os.path.isabs(p):
            p = os.path.join(cwd, p)
        return [p or cwd], cwd
    if tool in ("Bash", "PowerShell"):
        cmd = ti.get("command") or ""
        if not re.search(_SEARCH_CMD, cmd, re.I):
            return [], cwd
        return [cmd, cwd], cwd
    return [], cwd


def _marker(session, root):
    import hashlib
    import re
    import tempfile
    import time
    from pathlib import Path
    from . import engine as eng
    d = Path(tempfile.gettempdir()) / "ue-index-hook"
    d.mkdir(exist_ok=True)
    try:  # tidy markers from old sessions
        cutoff = time.time() - 2 * 86400
        for f in d.iterdir():
            if f.stat().st_mtime < cutoff:
                f.unlink()
    except OSError:
        pass
    return d / ("%s-%s" % (re.sub(r"\W", "", session)[:64], hashlib.sha1(eng.norm(root).encode()).hexdigest()[:10]))


def run():
    raw = sys.stdin.read()
    low = raw.lower()
    if not any(w in low for w in _MAYBE_SEARCH):
        return 0
    import json
    import re
    from . import engine as eng
    try:
        data = json.loads(raw)
    except ValueError:
        return 0
    targets, cwd = _targets(data)
    if not targets:
        return 0
    tool = data.get("tool_name")
    # Git Bash / MSYS spell C:\foo as /c/foo.
    blob = [eng.norm(re.sub(r"(^|[\s\"'=(])/([a-zA-Z])/", r"\1\2:/", t)) for t in targets]
    hit = None
    for _, root in eng.known_engines():
        nroot = eng.norm(root)
        for i, t in enumerate(blob):
            if tool in ("Bash", "PowerShell") and i == 1:
                # cwd inside the engine only counts if the command has no absolute path elsewhere
                if t.startswith(nroot + "/") or t == nroot:
                    hit = root
            elif nroot in t and not any(x in t for x in ("/.claude/ue-index", "/.claude/skills", "/.claude/plugins")):
                hit = root
        if hit:
            break
    if not hit:
        return 0
    try:
        engine = eng.Engine(hit)
    except eng.EngineError:
        return 0
    idx = engine.index_dir()
    for t in blob:
        if eng.norm(idx) in t:
            return 0  # already searching the index itself
    marker = _marker(data.get("session_id") or "nosession", hit)
    if marker.exists():
        return 0
    state, detail = eng.status(engine)
    w = _wrapper(tool)
    if state == "fresh":
        msg = ("UE source index exists for %s (%s %s): %s. Before searching engine source, grep its symbols.tsv / functions.tsv / "
               "cvars.tsv / logs.tsv / config.tsv, check NOTES.md for the module, or run `%s find <name>`. Read INDEX.md "
               "if you haven't. The ue-source-index skill has the details." % (engine.root, engine.kind, engine.version, idx, w))
    elif state == "building":
        msg = "A UE source index for %s is being built (%s). Check `%s status` before a broad engine search." % (engine.root, detail, w)
    else:
        msg = ("No up-to-date UE source index for %s (%s: %s). Use the ue-source-index skill: start `%s build --engine \"%s\"` in the "
               "background (it takes minutes) and tell the user, then keep engine searches narrow until it finishes."
               % (engine.root, state, detail, w, engine.root))
    try:
        marker.touch()
    except OSError:
        pass
    print(json.dumps({"hookSpecificOutput": {"hookEventName": "PreToolUse", "additionalContext": msg}}))
    return 0
