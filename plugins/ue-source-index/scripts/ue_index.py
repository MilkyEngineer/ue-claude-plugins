#!/usr/bin/env python3
"""Unreal Engine source index CLI. Run via ue_index.cmd (Windows) or ue_index.sh, which locate a Python 3.

  status   [--engine E | --project P]        state of the index for the engine (exit 0 fresh, 2 missing, 3 stale, 4 building)
  build    [--engine E | --project P] [--jobs N] [--full] [--force]
  find     <term> [--kind symbols,functions,cvars,logs,config,deprecated] [--limit N] [--exact] [--regex]
  module   <Name> [--dependents]
  note     add --module M --symbol S --anchor file:line "text" | replace <id> ... | remove <id> | check | list [--module M]
  engines                                     list engines known to the launcher/registry
  hook                                        PreToolUse hook entry point (reads JSON on stdin)
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from ueidx import engine as eng  # noqa: E402

EXIT = {"fresh": 0, "missing": 2, "stale": 3, "building": 4}


def _engine(a):
    return eng.find_engine(getattr(a, "engine", None), getattr(a, "project", None))


def _require_index(e):
    state, detail = eng.status(e)
    if state == "missing":
        raise eng.EngineError("no index for %s: run `build` first" % e.root)
    if state != "fresh":
        print("warning: index is %s (%s); results may be out of date" % (state, detail), file=sys.stderr)
    return e.index_dir()


def cmd_status(a):
    e = _engine(a)
    state, detail = eng.status(e)
    eng.register(e)
    print("ENGINE %s" % e.describe())
    print("FOUND  %s" % e.how)
    print("INDEX  %s" % e.index_dir())
    print("STATUS %s (%s)" % (state, detail))
    if state == "fresh":
        print("NEXT   grep the .tsv files / read INDEX.md; `find <name>` for ranked lookups")
    elif state in ("missing", "stale"):
        print("NEXT   build (run in background; minutes): %s build --engine \"%s\"" % (_wrapper_name(), e.root))
    return EXIT[state]


def _wrapper_name():
    here = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")
    return "sh %s/ue_index.sh" % here


def cmd_build(a):
    from ueidx import build
    build.build(_engine(a), jobs=a.jobs, full=a.full, force=a.force)
    return 0


def cmd_find(a):
    from ueidx import query
    d = _require_index(_engine(a))
    kinds = set(a.kind.split(",")) if a.kind else None
    out = query.find(d, a.term, kinds=kinds, limit=a.limit, exact=a.exact, regex=a.regex)
    print("\n".join(out) if out else "no matches for %r (try --regex, or grep the .tsv files)" % a.term)
    return 0 if out else 1


def cmd_module(a):
    from ueidx import query
    d = _require_index(_engine(a))
    out = query.module_info(d, a.name, a.dependents)
    print("\n".join(out) if out else "no module named %r" % a.name)
    return 0 if out else 1


def cmd_note(a):
    from ueidx import notes
    e = _engine(a)
    try:
        if a.action in ("add", "replace"):
            if not (a.module and a.anchor and a.text):
                raise notes.NoteError("add/replace need --module, --anchor and the note text")
            if a.action == "replace" and not a.id:
                raise notes.NoteError("replace needs the note id, e.g. `note replace n004 ...`")
            n = notes.add(e, a.module, a.symbol, a.anchor, " ".join(a.text), replace_id=a.id if a.action == "replace" else None)
            print("saved: " + n.render())
        elif a.action == "remove":
            notes.remove(e, a.id)
            print("removed %s" % a.id)
        elif a.action == "check":
            print(notes.check(e))
        elif a.action == "list":
            for n in notes.load(notes.notes_path(e)):
                if not a.module or n.module.lower() == a.module.lower():
                    print(n.render())
    except notes.NoteError as ex:
        print("note rejected: %s" % ex, file=sys.stderr)
        return 1
    return 0


def cmd_engines(a):
    print(eng.format_known())
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(prog="ue_index", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    def common(sp):
        sp.add_argument("--engine", help="engine root path, or an association (5.7, {GUID}, custom build name)")
        sp.add_argument("--project", help=".uproject whose EngineAssociation selects the engine")

    sp = sub.add_parser("status"); common(sp); sp.set_defaults(fn=cmd_status)
    sp = sub.add_parser("build"); common(sp)
    sp.add_argument("--jobs", type=int, default=None, help="parser processes (default: cores-1, max 8; use 1-2 on a slow HDD)")
    sp.add_argument("--full", action="store_true", help="ignore the per-module cache")
    sp.add_argument("--force", action="store_true", help="take over a lock left by a crashed build")
    sp.set_defaults(fn=cmd_build)
    sp = sub.add_parser("find"); common(sp)
    sp.add_argument("term")
    sp.add_argument("--kind", help="comma list of: symbols,functions,cvars,logs,config,deprecated")
    sp.add_argument("--limit", type=int, default=20)
    sp.add_argument("--exact", action="store_true", help="exact name (or Outer::name) matches only")
    sp.add_argument("--regex", action="store_true", help="treat term as a case-insensitive regex over the name columns")
    sp.set_defaults(fn=cmd_find)
    sp = sub.add_parser("module"); common(sp)
    sp.add_argument("name")
    sp.add_argument("--dependents", action="store_true", help="also list modules that depend on it")
    sp.set_defaults(fn=cmd_module)
    sp = sub.add_parser("note"); common(sp)
    sp.add_argument("action", choices=["add", "replace", "remove", "check", "list"])
    sp.add_argument("id", nargs="?", help="note id for replace/remove")
    sp.add_argument("--module")
    sp.add_argument("--symbol", default="-")
    sp.add_argument("--anchor", help="file:line (engine-relative or absolute)")
    sp.add_argument("--text", nargs="+", help="the finding (<= 280 chars)")
    sp.set_defaults(fn=cmd_note)
    sp = sub.add_parser("engines"); sp.set_defaults(fn=cmd_engines)
    sp = sub.add_parser("hook"); sp.set_defaults(fn=None)

    a = p.parse_args(argv)
    if a.cmd == "hook":
        from ueidx import hook
        try:
            return hook.run()
        except Exception:
            return 0
    try:
        return a.fn(a)
    except eng.EngineError as ex:
        print("error: %s" % ex, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
