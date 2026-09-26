"""Scans an engine tree, parses modules in parallel (with a per-module cache) and writes the index."""
import hashlib
import multiprocessing
import os
import shutil
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

from . import engine as eng
from . import parse
from .curated import CORE_MODULES

SRC_EXT = {".h": True, ".hpp": True, ".inl": True, ".cpp": False, ".cc": False}
ALWAYS_PRUNE = {"binaries", "intermediate", "saved", "thirdparty", "node_modules", "__pycache__"}
# Skipped unless the folder is itself a module (e.g. Source/Developer/DerivedDataCache) or sits inside one.
PRUNE_OUTSIDE_MODULES = {"content", "deriveddatacache", "documentation", "extras", "shaders", "resources", "docs", "doc"}
REFLECTED_KINDS = {"uclass", "ustruct", "uenum", "uiface"}
TYPE_KINDS = {"class", "struct", "union", "enum", "cpp-class", "cpp-struct", "cpp-union"} | REFLECTED_KINDS

TSV_HEADERS = {
    "symbols": "name\tkind\tmodule\tfile:line\tdetail",
    "functions": "name\tmodule\tfile:line\tsignature",
    "cvars": "name\tkind\tmodule\tfile:line\ttype\tflags\thelp",
    "logs": "name\tkind\tmodule\tfile:line\tverbosity",
    "config": "section\tkey\tsource\tmodule\tfile:line\tdetail",
    "deprecated": "name\tsince\tmodule\tfile:line\tmessage",
}


def log(msg):
    print("[ue-index] " + msg, flush=True)


def clean(v):
    return str(v).replace("\t", " ").replace("\r", " ").replace("\n", " ")


def tsv_line(fields):
    return "\t".join(clean(f) for f in fields) + "\n"


# ------------------------------------------------------------------ scan

def scan(engine, beat=lambda: None):
    root = engine.root
    root_s = str(root)
    rel = lambda p: os.path.relpath(p, root_s).replace("\\", "/")
    modules, plugins, inis = [], [], []
    stack = [(str(engine.engine_dir), None, None)]
    while stack:
        path, mod, plug = stack.pop()
        beat()
        try:
            entries = list(os.scandir(path))
        except OSError:
            continue
        files = [e for e in entries if e.is_file()]
        for e in files:
            if e.name.endswith(".uplugin"):
                data = eng.read_json(e.path) or {}
                plug = {
                    "name": e.name[:-8], "dir": rel(path), "friendly": data.get("FriendlyName", ""),
                    "desc": data.get("Description", ""), "category": data.get("Category", ""),
                    "mod_types": {m.get("Name"): m.get("Type", "") for m in data.get("Modules", []) if isinstance(m, dict)},
                }
                plugins.append(plug)
                break
        for e in files:
            if e.name.endswith(".Build.cs"):
                try:
                    deps = parse.parse_build_cs(parse.read_text(e.path))
                except OSError:
                    deps = {"Public": [], "Private": []}
                mod = {"name": e.name[:-9], "dir": rel(path), "plugin": plug["name"] if plug else "",
                       "plugin_dir": plug["dir"] if plug else "", "files": [],
                       "public_deps": deps["Public"], "private_deps": deps["Private"],
                       "type": _module_type(rel(path), plug, e.name[:-9])}
                modules.append(mod)
                break
        if os.path.basename(path).lower() == "config":
            for e in files:
                if e.name.lower().endswith(".ini"):
                    inis.append((e.path, plug["name"] if plug else "Engine"))
        if mod is not None:
            for e in files:
                ext = os.path.splitext(e.name)[1].lower()
                if ext in SRC_EXT:
                    try:
                        st = e.stat()
                        mod["files"].append((rel(e.path), st.st_size, st.st_mtime_ns))
                    except OSError:
                        pass
        for e in entries:
            name = e.name.lower()
            if not e.is_dir() or name.startswith(".") or name in ALWAYS_PRUNE:
                continue
            if mod is None and name in PRUNE_OUTSIDE_MODULES and not _has_build_cs(e.path):
                continue
            stack.append((e.path, mod, plug))
    # PluginBrowser ships placeholder template modules (PLUGIN_NAME etc.); they are not real code.
    modules = [m for m in modules if "PLUGIN_NAME" not in m["name"] and "/Templates/" not in m["dir"]]
    modules.sort(key=lambda m: m["dir"].lower())
    for m in modules:
        m["files"].sort()
    # Assign doc filenames, disambiguating duplicate module names (case-insensitively: Windows filesystems).
    counts = Counter(m["name"].lower() for m in modules)
    used = set()
    for m in modules:
        base = m["name"] if counts[m["name"].lower()] == 1 else "%s__%s" % (m["name"], m["plugin"] or m["dir"].split("/")[2])
        doc, n = base, 1
        while doc.lower() in used:
            n += 1
            doc = "%s_%d" % (base, n)
        used.add(doc.lower())
        m["doc"] = "modules/%s.md" % doc
        m["purpose"] = _purpose(m, plugins)
    return modules, plugins, inis


def _has_build_cs(path):
    try:
        return any(e.name.endswith(".Build.cs") for e in os.scandir(path))
    except OSError:
        return False


def _module_type(reldir, plug, name):
    parts = reldir.split("/")
    if plug:
        return plug["mod_types"].get(name) or "Plugin"
    if "Source" in parts:
        i = parts.index("Source")
        if i + 1 < len(parts):
            return parts[i + 1]
    return "?"


def _purpose(m, plugins):
    if m["name"] in CORE_MODULES:
        return CORE_MODULES[m["name"]]
    if m["plugin"]:
        p = next((p for p in plugins if p["name"] == m["plugin"]), None)
        if p:
            desc = p["desc"] or p["friendly"]
            if m["name"] != m["plugin"] and p["friendly"] and p["desc"]:
                desc = "%s: %s" % (p["friendly"], p["desc"])
            return parse.squash(desc, 160)
    return ""


def module_fp(m):
    h = hashlib.sha1()
    for f, size, mt in m["files"]:
        h.update(("%s|%d|%d\n" % (f, size, mt)).encode())
    return h.hexdigest()


def module_key(m):
    return "%s_%s" % (m["name"], hashlib.sha1(m["dir"].lower().encode()).hexdigest()[:10])


# ------------------------------------------------------------------ worker

def parse_module(job):
    root, m = job
    out = {k: [] for k in ("symbols", "functions", "logs", "cvars", "config", "deprecated", "uclass", "cfgprops")}
    out["errors"] = []
    name = m["name"]
    for f, _, _ in m["files"]:
        ext = os.path.splitext(f)[1].lower()
        try:
            text = parse.read_text(os.path.join(root, f))
        except OSError as e:
            out["errors"].append("%s: %s" % (f, e))
            continue
        try:
            r = parse.parse_source(text, SRC_EXT[ext], name)
        except Exception as e:  # never let one odd file kill the build
            out["errors"].append("%s: %s: %s" % (f, type(e).__name__, e))
            continue
        loc = lambda ln: "%s:%d" % (f, ln)
        out["symbols"] += [[n, k, name, loc(ln), d] for n, k, ln, d in r.symbols]
        out["functions"] += [[n, name, loc(ln), s] for n, ln, s in r.funcs]
        out["logs"] += [[n, k, name, loc(ln), v] for n, k, ln, v in r.logs]
        out["cvars"] += [[n, k, name, loc(ln), t, fl, h] for n, k, ln, t, fl, h in r.cvars]
        out["config"] += [[s, k, src, name, loc(ln), d] for s, k, src, ln, d in r.config]
        out["deprecated"] += [[n, v, name, loc(ln), msg] for n, v, ln, msg in r.depr]
        out["uclass"] += [[n, c, dc, b, name] for n, c, dc, b in r.uclass]
        out["cfgprops"] += [[c, p, t, name, loc(ln)] for c, p, t, ln in r.cfgprops]
    return m, out


# ------------------------------------------------------------------ build

class Lock:
    def __init__(self, engine, force):
        self.path = eng.lock_path(engine)
        self.force = force

    def __enter__(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        for attempt in range(2):
            try:
                fd = os.open(str(self.path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.write(fd, eng.lock_text().encode())
                os.close(fd)
                self.last_beat = time.time()
                return self
            except FileExistsError:
                lk = eng.read_lock_at(self.path)
                if lk is None:
                    continue  # vanished between open and read: retry
                if attempt == 0 and (self.force or not lk["live"]):
                    try:
                        self.path.unlink()
                    except OSError:
                        pass
                    continue
                raise eng.EngineError("another build is running (%s, heartbeat %ds ago). If it is wedged, rerun with --force."
                                      % (lk["info"], lk["age"]))
        raise eng.EngineError("could not acquire %s" % self.path)

    def beat(self):
        """Heartbeat: keeps the lock's mtime fresh so other processes can tell this build is alive."""
        now = time.time()
        if now - self.last_beat >= eng.LOCK_HEARTBEAT_SECONDS:
            try:
                os.utime(self.path)
            except OSError:
                pass
            self.last_beat = now

    def __exit__(self, *a):
        try:
            self.path.unlink()
        except OSError:
            pass


def build(engine, jobs=None, full=False, force=False):
    from . import notes
    d = engine.index_dir()
    t0 = time.time()
    with Lock(engine, force) as lock:
        log("engine %s" % engine.describe())
        log("index  %s" % d)
        modules, plugins, inis = scan(engine, lock.beat)
        nfiles = sum(len(m["files"]) for m in modules)
        log("scanned %d modules, %d plugins, %d source files, %d ini files in %.0fs"
            % (len(modules), len(plugins), nfiles, len(inis), time.time() - t0))

        cache_dir = d / ".cache" / "modules"
        cache_dir.mkdir(parents=True, exist_ok=True)
        staging = d / ".staging"
        shutil.rmtree(staging, ignore_errors=True)
        (staging / "modules").mkdir(parents=True)
        writers = {k: open(staging / ("%s.tsv" % k), "w", encoding="utf-8", newline="\n") for k in TSV_HEADERS}
        for k, w in writers.items():
            w.write("#" + TSV_HEADERS[k] + "\n")

        state = {"uclass": {}, "cfgprops": [], "errors": [], "totals": Counter()}

        def emit(m, out):
            lock.beat()
            for k in TSV_HEADERS:
                if k == "config":
                    continue
                w = writers[k]
                for row in out[k]:
                    w.write(tsv_line(row))
                state["totals"][k] += len(out[k])
            for row in out["config"]:
                writers["config"].write(tsv_line(row))
            state["totals"]["config"] += len(out["config"])
            for n, c, dc, b, mod in out["uclass"]:
                state["uclass"].setdefault(n, (c, dc, b, mod))
            state["cfgprops"] += out["cfgprops"]
            state["errors"] += out["errors"]
            write_module_doc(staging, m, out)

        todo, cached = [], 0
        for m in modules:
            key, fp = module_key(m), module_fp(m)
            c = None if full else eng.read_json(cache_dir / (key + ".json"))
            if c and c.get("fp") == fp and c.get("schema") == eng.SCHEMA:
                emit(m, c["out"])
                cached += 1
            else:
                todo.append(m)
        if cached:
            log("reused %d unchanged modules from cache; parsing %d" % (cached, len(todo)))

        jobs = jobs or max(1, min(8, (os.cpu_count() or 2) - 1))
        total_files = sum(len(m["files"]) for m in todo)
        done_files, next_report, t1 = 0, 0.1, time.time()
        pool = multiprocessing.Pool(jobs) if jobs > 1 and len(todo) > 4 else None
        try:
            it = pool.imap_unordered(parse_module, [(str(engine.root), m) for m in todo], chunksize=1) if pool \
                else map(parse_module, [(str(engine.root), m) for m in todo])
            for m, out in it:
                emit(m, out)
                # Per-module cache: makes source-build rebuilds incremental, and lets an interrupted
                # installed-build run resume instead of starting over.
                eng.write_json(cache_dir / (module_key(m) + ".json"),
                               {"fp": module_fp(m), "schema": eng.SCHEMA, "out": out}, compact=True)
                done_files += len(m["files"])
                if total_files and done_files / total_files >= next_report:
                    el = time.time() - t1
                    frac = done_files / total_files
                    log("parsed %3.0f%% (%d/%d files) %.0fs elapsed, ~%.0fs left"
                        % (frac * 100, done_files, total_files, el, el / frac - el))
                    next_report = frac + 0.1
        except BaseException:
            if pool:
                pool.terminate()  # don't let queued modules keep parsing after an error or Ctrl+C
            raise
        else:
            if pool:
                pool.close()
        finally:
            if pool:
                pool.join()

        # Config: UPROPERTY(Config) resolved through the UCLASS hierarchy, then ini defaults.
        cw = writers["config"]
        for cls, prop, typ, mod, loc in state["cfgprops"]:
            cfg, dc, n, seen = "", 0, cls, 0
            while n and seen < 30:
                info = state["uclass"].get(n)
                if not info:
                    break
                if info[0]:
                    cfg, dc = info[0], info[1]
                    break
                n, seen = info[2], seen + 1
            short = cls[1:] if len(cls) > 1 and cls[0] in "UA" and cls[1].isupper() else cls
            cw.write(tsv_line(["/Script/%s.%s" % (mod, short), prop, "uprop", mod, loc,
                               "ini=%s%s type=%s" % (cfg or "?", " defaultconfig" if dc else "", typ)]))
            state["totals"]["config"] += 1
        for path, owner in sorted(inis):
            try:
                rows = parse.parse_ini(parse.read_text(path))
            except OSError:
                continue
            relp = os.path.relpath(path, str(engine.root)).replace("\\", "/")
            for sec, key, ln, val in rows:
                cw.write(tsv_line([sec, key, "ini", owner, "%s:%d" % (relp, ln), val]))
            state["totals"]["config"] += len(rows)
        for w in writers.values():
            w.close()

        with open(staging / "modules.tsv", "w", encoding="utf-8", newline="\n") as f:
            f.write("#name\ttype\tplugin\tpath\tdoc\tpublic_deps\tprivate_deps\tpurpose\n")
            for m in modules:
                f.write(tsv_line([m["name"], m["type"], m["plugin"] or "-", m["dir"], m["doc"],
                                  ",".join(m["public_deps"]), ",".join(m["private_deps"]), m["purpose"]]))
        write_index_md(staging, engine, modules, plugins, state)
        if state["errors"]:
            with open(staging / "build-errors.txt", "w", encoding="utf-8") as f:
                f.write("\n".join(state["errors"]) + "\n")

        # Swap staging into place. Flag the existing index as incomplete first (rather than deleting meta.json)
        # so a failed swap reads as "stale" and stays queryable, and write the real meta.json last.
        old_meta = eng.read_json(d / "meta.json")
        if old_meta:
            old_meta["incomplete"] = True
            eng.write_json(d / "meta.json", old_meta)
        try:
            swap(staging, d)
        except OSError as e:
            raise eng.EngineError("could not move the new index into place (%s). Something probably had an index file "
                                  "open. Run build again; parsed modules are cached, so it resumes quickly." % e)
        if not state["errors"]:
            try:
                (d / "build-errors.txt").unlink()
            except OSError:
                pass
        if engine.installed:
            shutil.rmtree(d / ".cache", ignore_errors=True)  # installed builds never rebuild incrementally
        meta = {
            "schema": eng.SCHEMA, "engine_root": str(engine.root), "kind": engine.kind,
            "version": engine.version, "changelist": engine.changelist,
            "fingerprint": engine.fingerprint(), "built_at": time.strftime("%Y-%m-%d %H:%M"),
            "build_seconds": round(time.time() - t0), "modules": len(modules), "files": nfiles,
            "rows": dict(state["totals"]), "parse_errors": len(state["errors"]),
        }
        eng.write_json(d / "meta.json", meta)
        eng.register(engine)
        if not engine.installed:
            _git_exclude(engine)
        note_msg = notes.after_build(engine)
    log("done in %.0fs: %s" % (time.time() - t0, ", ".join("%s=%d" % kv for kv in sorted(state["totals"].items()))))
    if state["errors"]:
        log("%d files could not be parsed; see %s" % (len(state["errors"]), d / "build-errors.txt"))
    if note_msg:
        log(note_msg)
    log("read %s first" % (d / "INDEX.md"))


def _replace(src, dst, attempts=40):
    """os.replace, retrying on Windows sharing violations: a grep/ripgrep holding an index file open makes
    the replace fail until it closes the file, usually within a second or two."""
    for i in range(attempts):
        try:
            os.replace(src, dst)
            return
        except PermissionError:
            if i == attempts - 1:
                raise
            time.sleep(0.25)


def swap(staging, d):
    for item in list(staging.iterdir()):
        target = d / item.name
        if item.is_dir():
            old = d / (item.name + ".old")
            shutil.rmtree(old, ignore_errors=True)
            if target.exists():
                _replace(target, old)
            _replace(item, target)
            shutil.rmtree(old, ignore_errors=True)
        else:
            _replace(item, target)
    staging.rmdir()


def _git_exclude(engine):
    # UE's root .gitignore re-includes *.md, *.h etc. with `!` rules, which beat .git/info/exclude,
    # so a nested .gitignore is the only reliable way to keep the index out of git status.
    try:
        with open(engine.index_dir() / ".gitignore", "w", encoding="utf-8") as f:
            f.write("# generated by ue-source-index\n*\n")
    except OSError:
        pass


# ------------------------------------------------------------------ docs

def include_path(relfile):
    """Include path for a header, or None if it is private to its module."""
    p = "/" + relfile
    for marker in ("/Public/", "/Classes/", "/Internal/"):
        i = p.rfind(marker)
        if i >= 0:
            return p[i + len(marker):]
    return None


def write_module_doc(staging, m, out):
    types_by_file = defaultdict(list)
    counts = Counter()
    for name, kind, _, loc, detail in out["symbols"]:
        counts[kind] += 1
        if kind in TYPE_KINDS and "::" not in name:
            f = loc.rsplit(":", 1)[0]
            types_by_file[f].append(name + ("*" if kind in REFLECTED_KINDS else ""))
    headers = [f for f, _, _ in m["files"] if SRC_EXT[os.path.splitext(f)[1].lower()]]
    files = sorted(set(headers) | set(types_by_file))
    by_folder = defaultdict(list)
    for f in files:
        sub = f[len(m["dir"]) + 1:] if f.startswith(m["dir"] + "/") else f
        by_folder[os.path.dirname(sub) or "."].append((os.path.basename(sub), f))

    lines = ["# Module %s" % m["name"], ""]
    lines.append("- Type: %s | Plugin: %s | Path: %s" % (m["type"], m["plugin"] or "-", m["dir"]))
    if m["purpose"]:
        lines.append("- Purpose: %s" % m["purpose"])
    lines.append("- Build.cs: add \"%s\" to your dependency list. Public deps: %s" % (m["name"], ", ".join(m["public_deps"]) or "-"))
    lines.append("- Private deps: %s" % (", ".join(m["private_deps"]) or "-"))
    lines.append("- Stats: %d headers, %d types (%d reflected), %d UFUNCTIONs, %d UPROPERTYs, %d exported functions, %d CVars, %d log categories"
                 % (len(headers), sum(counts[k] for k in TYPE_KINDS), counts["uclass"] + counts["ustruct"] + counts["uenum"] + counts["uiface"],
                    counts["ufunc"], counts["uprop"], len(out["functions"]), len(out["cvars"]), len(out["logs"])))
    lines.append("- Notes: grep NOTES.md for `[%s]`" % m["name"])
    lines.append("- Include path = path after Public/, Classes/ or Internal/. Private/ headers are not includable from other modules. `*` = reflected (UCLASS/USTRUCT/UENUM/UINTERFACE).")
    if out["logs"]:
        lines.append("- Log categories: " + ", ".join(sorted(set(r[0] for r in out["logs"])))[:600])
    if out["cvars"]:
        names = sorted(set(r[0] for r in out["cvars"]))
        lines.append("- CVars/commands (%d): %s%s" % (len(names), ", ".join(names[:40]), " ..." if len(names) > 40 else ""))
    folders = sorted(by_folder)
    toc_at = len(lines) + 2
    body_start = toc_at + len(folders) + 2
    toc, body = [], []
    for folder in folders:
        toc.append("- %s (%d files) -> line %d" % (folder, len(by_folder[folder]), body_start + len(body) + 1))
        body.append("## %s" % folder)
        for base, f in sorted(by_folder[folder], key=lambda x: x[0].lower()):
            ts = types_by_file.get(f)
            body.append("- %s%s" % (base, (": " + ", ".join(ts)) if ts else ""))
        body.append("")
    lines += ["", "## Folders (Read with offset=line)"] + toc + ["", ""] + body
    with open(staging / m["doc"], "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def write_index_md(staging, engine, modules, plugins, state):
    d = engine.index_dir()
    t = state["totals"]
    area = Counter()
    for m in modules:
        parts = m["dir"].split("/")
        if parts[1] == "Plugins" and len(parts) > 2:
            area["Engine/Plugins/%s" % parts[2]] += 1
        elif parts[1] == "Source" and len(parts) > 2:
            area["Engine/Source/%s" % parts[2]] += 1
        else:
            area["/".join(parts[:3])] += 1
    by_name = {m["name"]: m for m in modules}
    L = []
    L.append("# UE source index: %s %s (CL %s)" % (engine.kind, engine.version, engine.changelist))
    L.append("")
    L.append("Engine root: `%s`  |  Index: `%s`  |  Built %s  |  schema %d" % (engine.root, d, time.strftime("%Y-%m-%d %H:%M"), eng.SCHEMA))
    L.append("All file paths in the index are relative to the engine root. Every row has `file:line`, so open the source at that line to confirm details.")
    L.append("")
    L.append("## Files. Grep the .tsv files; never Read them whole")
    L.append("")
    L.append("| File | Rows | Columns (tab-separated) | Grep example |")
    L.append("|---|---|---|---|")
    rows = [
        ("symbols.tsv", t["symbols"], TSV_HEADERS["symbols"], "`^AActor\\t` type; `^AActor::` members; `::BeginPlay\\t` member by name"),
        ("functions.tsv", t["functions"], TSV_HEADERS["functions"], "`^UWorld::SpawnActor` exported non-UFUNCTION functions"),
        ("cvars.tsv", t["cvars"], TSV_HEADERS["cvars"], "`^r\\.Nanite` or `(?i)shadow.*\\tvar\\t`"),
        ("logs.tsv", t["logs"], TSV_HEADERS["logs"], "`^LogNet\\t`"),
        ("config.tsv", t["config"], TSV_HEADERS["config"], "`\\tbUseFixedFrameRate\\t` (source = ini/code/uprop)"),
        ("deprecated.tsv", t["deprecated"], TSV_HEADERS["deprecated"], "`^UEngine::` / `SomeFunc`"),
        ("modules.tsv", len(modules), "name\ttype\tplugin\tpath\tdoc\tpublic_deps\tprivate_deps\tpurpose", "`^Niagara\\t`; `\\bGameplayTags\\b` for dependents"),
        ("modules/<Name>.md", len(modules), "per-module summary, deps, folder TOC, types per header", "Read the TOC, then Read with offset"),
        ("NOTES.md", "-", "hard-won findings, one line each: `nNNN [Module] Symbol - text @file:line#hash`", "`\\[Engine\\]` or a symbol name"),
    ]
    for r in rows:
        L.append("| %s | %s | %s | %s |" % (r[0], r[1], r[2].replace("\t", " · "), r[3]))
    L.append("")
    L.append("symbols.tsv kinds: uclass, ustruct, uenum, uiface (reflected); class, struct, enum, union (plain header types); "
             "cpp-class/cpp-struct (defined in a .cpp); ufunc; uprop; delegate; alias; gtag (native gameplay tag, name = tag string). "
             "Nested types and members are `Outer::Name`. For methods that are neither UFUNCTION nor *_API exported, find the class here and grep its header.")
    L.append("")
    L.append("Include rule: include path = header path after `Public/`, `Classes/` or `Internal/`; module name = Build.cs dependency. "
             "`Private/` headers can't be included from other modules.")
    L.append("")
    L.append("## Core module map")
    L.append("")
    for name, purpose in CORE_MODULES.items():
        m = by_name.get(name)
        if m:
            L.append("- **%s** (`%s`): %s" % (name, m["dir"], purpose))
    L.append("")
    L.append("## Areas (module counts)")
    L.append("")
    for a, n in sorted(area.items()):
        L.append("- %s: %d" % (a, n))
    L.append("")
    L.append("Not indexed: ThirdParty sources, shaders (Engine/Shaders/*.usf, *.ush), C# tooling (UBT/UAT), content. Search those directly.")
    with open(staging / "INDEX.md", "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(L) + "\n")


if __name__ == "__main__":
    sys.exit("run via ue_index.py")
