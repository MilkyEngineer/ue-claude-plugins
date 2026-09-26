"""`find` and `module` lookups: ranked, compact answers in a single call."""
import re

from .build import include_path

FILES = {
    # file: (name column indexes, label)
    "symbols": ([0], "symbol"),
    "functions": ([0], "function"),
    "cvars": ([0], "cvar"),
    "logs": ([0], "log"),
    "config": ([0, 1], "config"),
    "deprecated": ([0], "deprecated"),
}


def _score(value, term, lterm):
    """Lower is better; None = no match."""
    if value == term:
        return 0
    lv = value.lower()
    if lv == lterm:
        return 1
    if lv.endswith("::" + lterm):
        return 2
    if lv.startswith(lterm):
        return 3
    if "::" + lterm in lv:
        return 4
    if lterm in lv:
        return 5
    return None


def find(index_dir, term, kinds=None, limit=20, exact=False, regex=False):
    lterm = term.lower()
    rx = re.compile(term, re.I) if regex else None
    results = []
    for fname, (cols, label) in FILES.items():
        if kinds and fname not in kinds:
            continue
        try:
            f = open(index_dir / ("%s.tsv" % fname), encoding="utf-8")
        except OSError:
            continue
        with f:
            for line in f:
                if line.startswith("#"):
                    continue
                # Cheap pre-filter before splitting.
                if not rx and lterm not in line.lower():
                    continue
                row = line.rstrip("\n").split("\t")
                best = None
                for c in cols:
                    if c >= len(row):
                        continue
                    if rx:
                        s = 3 if rx.search(row[c]) else None
                    else:
                        s = _score(row[c], term, lterm)
                    if s is not None and (best is None or s < best):
                        best = s
                if best is None or (exact and best > 2):
                    continue
                results.append((best, label, row))
    results.sort(key=lambda r: (r[0], len(r[2][0])))
    out = []
    for score, label, row in results[:limit]:
        out.append(format_row(label, row))
    more = len(results) - limit
    if more > 0:
        out.append("... %d more matches (narrow with --kind, --exact, or grep the .tsv directly)" % more)
    return out


def format_row(label, row):
    if label == "symbol":
        name, kind, module, loc, detail = (row + [""] * 5)[:5]
        s = "%s  [%s] %s  %s" % (name, kind, module, loc)
        if detail:
            s += "\n    " + detail
        if kind in ("class", "struct", "enum", "union", "uclass", "ustruct", "uenum", "uiface", "delegate", "alias"):
            inc = include_path(loc.rsplit(":", 1)[0])
            s += "\n    " + ('#include "%s"  (Build.cs: "%s")' % (inc, module) if inc else "private header in %s: not includable from other modules" % module)
        return s
    if label == "function":
        name, module, loc, sig = (row + [""] * 4)[:4]
        return "%s  [func] %s  %s\n    %s" % (name, module, loc, sig)
    if label == "cvar":
        name, kind, module, loc, typ, flags, help_ = (row + [""] * 7)[:7]
        return "%s  [cvar-%s %s] %s  %s%s\n    %s" % (name, kind, typ, module, loc, ("  flags=" + flags) if flags else "", help_)
    if label == "log":
        name, kind, module, loc, verb = (row + [""] * 5)[:5]
        return "%s  [log %s %s] %s  %s" % (name, kind, verb, module, loc)
    if label == "config":
        sec, key, src, module, loc, detail = (row + [""] * 6)[:6]
        return "[%s] %s  (%s) %s  %s\n    %s" % (sec, key, src, module, loc, detail)
    if label == "deprecated":
        name, since, module, loc, msg = (row + [""] * 5)[:5]
        return "%s  [deprecated %s] %s  %s\n    %s" % (name, since, module, loc, msg)
    return "\t".join(row)


def module_info(index_dir, name, dependents=False):
    rows, users = [], []
    ln = name.lower()
    with open(index_dir / "modules.tsv", encoding="utf-8") as f:
        for line in f:
            if line.startswith("#"):
                continue
            row = line.rstrip("\n").split("\t")
            if row[0].lower() == ln:
                rows.append(row)
            elif dependents and (name in row[5].split(",") or name in row[6].split(",")):
                users.append("%s (%s)" % (row[0], "public" if name in row[5].split(",") else "private"))
    out = []
    for r in rows:
        name_, typ, plugin, path, doc, pub, priv, purpose = (r + [""] * 8)[:8]
        out.append("%s  [%s] plugin=%s\n  path: %s\n  doc:  %s\n  public deps: %s\n  private deps: %s%s"
                   % (name_, typ, plugin, path, index_dir / doc, pub or "-", priv or "-", ("\n  purpose: " + purpose) if purpose else ""))
    if dependents:
        out.append("dependents (%d): %s" % (len(users), ", ".join(sorted(users)) or "-"))
    return out
