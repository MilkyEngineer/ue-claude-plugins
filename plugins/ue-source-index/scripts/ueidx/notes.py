"""NOTES.md: short, anchored, grep-only findings. Every write goes through here so the limits hold."""
import difflib
import hashlib
import os
import re
from pathlib import Path

from . import engine as eng

MAX_LEN = 280
MAX_PER_MODULE = 15
WINDOW = 2  # lines either side of the anchor that are hashed

HEADER = """# NOTES: hard-won findings for this engine version

Never Read this file whole: grep it for `[Module]` or a symbol. Managed by `ue_index note ...`, so don't hand-edit.
Only record what took real digging or where the obvious answer is wrong. Point at code rather than explain it.
Format: `nNNN [Module] Symbol - finding @file:line#anchorhash`. A `[STALE?]` marker means the anchored code changed, so re-verify before trusting it.

"""

LINE_RE = re.compile(r"^(?P<id>n\d+) (?P<stale>\[STALE\?\] )?\[(?P<module>[^\]]+)\] (?P<symbol>\S+) - (?P<text>.*) @(?P<file>[^\s#]+):(?P<line>\d+)#(?P<hash>[0-9a-f]{8})$")


class NoteError(Exception):
    pass


class Note:
    def __init__(self, id, module, symbol, text, file, line, hash, stale=False):
        self.id, self.module, self.symbol, self.text = id, module, symbol, text
        self.file, self.line, self.hash, self.stale = file, int(line), hash, stale

    def render(self):
        return "%s %s[%s] %s - %s @%s:%d#%s" % (self.id, "[STALE?] " if self.stale else "", self.module,
                                                 self.symbol, self.text, self.file, self.line, self.hash)


def notes_path(engine):
    return engine.index_dir() / "NOTES.md"


def load(path):
    notes = []
    try:
        for line in Path(path).read_text(encoding="utf-8").splitlines():
            m = LINE_RE.match(line)
            if m:
                notes.append(Note(m["id"], m["module"], m["symbol"], m["text"], m["file"], m["line"], m["hash"], bool(m["stale"])))
    except OSError:
        pass
    return notes


def save(path, notes):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    notes = sorted(notes, key=lambda n: (n.module.lower(), n.symbol.lower(), n.id))
    tmp = path.with_suffix(".tmp")
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER + "".join(n.render() + "\n" for n in notes))
    os.replace(tmp, path)


def _window_hash(lines, line):
    lo, hi = max(0, line - 1 - WINDOW), min(len(lines), line + WINDOW)
    if line < 1 or line > len(lines):
        return None
    chunk = "\n".join(re.sub(r"\s+", " ", l).strip() for l in lines[lo:hi])
    return hashlib.sha1(chunk.encode("utf-8")).hexdigest()[:8]


def _read_lines(engine, rel):
    try:
        with open(engine.root / rel, "r", encoding="utf-8", errors="replace") as f:
            return f.read().splitlines()
    except OSError:
        return None


def _normalise_anchor(engine, anchor):
    m = re.match(r"^(.*):(\d+)$", anchor.strip())
    if not m:
        raise NoteError("anchor must be <file>:<line>, got %r" % anchor)
    p = Path(m.group(1))
    if p.is_absolute():
        try:
            rel = os.path.relpath(p, engine.root)
        except ValueError:
            raise NoteError("anchor file is not inside the engine root %s" % engine.root)
    else:
        rel = str(p)
    rel = rel.replace("\\", "/")
    if rel.startswith(".."):
        raise NoteError("anchor file is not inside the engine root %s" % engine.root)
    return rel, int(m.group(2))


def _module_names(engine):
    names = set()
    try:
        with open(engine.index_dir() / "modules.tsv", encoding="utf-8") as f:
            for line in f:
                if not line.startswith("#"):
                    names.add(line.split("\t", 1)[0])
    except OSError:
        pass
    return names


def _next_id(notes):
    n = max([int(x.id[1:]) for x in notes] or [0]) + 1
    return "n%03d" % n


def add(engine, module, symbol, anchor, text, replace_id=None):
    text = re.sub(r"\s+", " ", text).strip().replace(" @", " at ")
    symbol = (symbol or "-").strip().replace(" ", "")
    if len(text) > MAX_LEN:
        raise NoteError("note is %d chars; the limit is %d. State the finding and point at code; don't explain it." % (len(text), MAX_LEN))
    if len(text) < 15:
        raise NoteError("note is too short to be useful")
    mods = _module_names(engine)
    if mods and module not in mods:
        close = difflib.get_close_matches(module, list(mods), n=3)
        raise NoteError("unknown module %r (from modules.tsv). Did you mean: %s" % (module, ", ".join(close) or "?"))
    rel, line = _normalise_anchor(engine, anchor)
    lines = _read_lines(engine, rel)
    if lines is None:
        raise NoteError("anchor file not found: %s" % rel)
    h = _window_hash(lines, line)
    if h is None:
        raise NoteError("anchor line %d is outside %s (%d lines)" % (line, rel, len(lines)))

    path = notes_path(engine)
    notes = load(path)
    if replace_id:
        if not any(n.id == replace_id for n in notes):
            raise NoteError("no note %s" % replace_id)
        notes = [n for n in notes if n.id != replace_id]
    same_mod = [n for n in notes if n.module == module]
    for n in same_mod:
        ratio = difflib.SequenceMatcher(None, n.text.lower(), text.lower()).ratio()
        if ratio >= 0.8 or (n.file == rel and abs(n.line - line) <= 3 and n.symbol == symbol):
            raise NoteError("near-duplicate of existing note; use `note replace %s` if it needs correcting:\n%s" % (n.id, n.render()))
    if len(same_mod) >= MAX_PER_MODULE:
        raise NoteError("[%s] already has %d notes (the cap). Replace the least useful one with `note replace <id> ...`:\n%s"
                        % (module, len(same_mod), "\n".join(n.render() for n in same_mod)))
    note = Note(replace_id or _next_id(load(path)), module, symbol, text, rel, line, h)
    notes.append(note)
    save(path, notes)
    return note


def remove(engine, note_id):
    path = notes_path(engine)
    notes = load(path)
    keep = [n for n in notes if n.id != note_id]
    if len(keep) == len(notes):
        raise NoteError("no note %s" % note_id)
    save(path, keep)


def verify(engine, notes):
    """Re-anchor notes against `engine`. Returns (kept, relocated, stale, dropped) counts; mutates notes."""
    kept, stats = [], {"ok": 0, "relocated": 0, "stale": 0, "dropped": 0}
    cache = {}
    for n in notes:
        if n.file not in cache:
            cache[n.file] = _read_lines(engine, n.file)
        lines = cache[n.file]
        if lines is None:
            stats["dropped"] += 1
            continue
        if _window_hash(lines, n.line) == n.hash:
            n.stale = False
            stats["ok"] += 1
        else:
            found = None
            for cand in sorted(range(1, len(lines) + 1), key=lambda c: abs(c - n.line)):
                if _window_hash(lines, cand) == n.hash:
                    found = cand
                    break
            if found:
                n.line, n.stale = found, False
                stats["relocated"] += 1
            else:
                n.stale = True
                stats["stale"] += 1
        kept.append(n)
    return kept, stats


def check(engine):
    path = notes_path(engine)
    notes = load(path)
    if not notes:
        return "no notes"
    kept, st = verify(engine, notes)
    save(path, kept)
    return "notes: %(ok)d ok, %(relocated)d relocated, %(stale)d marked [STALE?], %(dropped)d dropped (file gone)" % st


def _version_tuple(name):
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)-CL(\d+)$", name)
    return tuple(int(x) for x in m.groups()) if m else None


def after_build(engine):
    """Verify existing notes, or seed a fresh installed-build index from the nearest older version's notes."""
    path = notes_path(engine)
    if path.is_file():
        return check(engine)
    if not engine.installed:
        return None
    me = _version_tuple(engine.index_dir().name)
    candidates = []
    for d in eng.HOME_INDEX.iterdir() if eng.HOME_INDEX.is_dir() else []:
        v = _version_tuple(d.name)
        if v and d != engine.index_dir() and (d / "NOTES.md").is_file():
            candidates.append((v, d))
    if not candidates:
        return None
    # Prefer the newest version not newer than this one, else the oldest newer one.
    older = [c for c in candidates if me is None or c[0] <= me]
    src = max(older)[1] if older else min(candidates)[1]
    notes = load(src / "NOTES.md")
    kept, st = verify(engine, notes)
    kept = [n for n in kept if not n.stale]  # only carry notes whose anchors still match exactly
    if kept:
        save(path, kept)
    return "carried %d notes forward from %s (%d stale/missing not carried)" % (len(kept), src.name, st["stale"] + st["dropped"])
