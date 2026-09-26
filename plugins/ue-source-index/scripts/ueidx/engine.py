"""Engine discovery, index location and freshness checks."""
import hashlib
import json
import os
import re
import sys
import time
from pathlib import Path

# Bump whenever the on-disk index format or parser output changes; forces a rebuild.
SCHEMA = 1

HOME_INDEX = Path(os.path.expanduser("~")) / ".claude" / "ue-index"
REGISTRY_FILE = HOME_INDEX / "engines.json"
LOCK_STALE_SECONDS = 3 * 3600


class EngineError(Exception):
    pass


def read_json(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            text = f.read()
    except OSError:
        return None
    try:
        return json.loads(text)
    except ValueError:
        # .uplugin / .uproject files sometimes carry trailing commas.
        try:
            return json.loads(re.sub(r",\s*([}\]])", r"\1", text))
        except ValueError:
            return None


def write_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, sort_keys=True)
    os.replace(tmp, path)


def norm(p):
    """Normalised string form of a path for case-insensitive prefix comparisons."""
    s = str(p).replace("\\", "/").rstrip("/")
    return s.lower() if os.name == "nt" else s


class Engine:
    def __init__(self, root, how=""):
        self.root = Path(root).resolve()
        self.engine_dir = self.root / "Engine"
        self.how = how
        bv = self.engine_dir / "Build" / "Build.version"
        if not bv.is_file():
            raise EngineError("%s is not an Unreal Engine root (no Engine/Build/Build.version)" % self.root)
        self.build_version = read_json(bv) or {}
        self.installed = (self.engine_dir / "Build" / "InstalledBuild.txt").is_file()

    @property
    def version(self):
        bv = self.build_version
        return "%s.%s.%s" % (bv.get("MajorVersion", "?"), bv.get("MinorVersion", "?"), bv.get("PatchVersion", "?"))

    @property
    def changelist(self):
        return self.build_version.get("Changelist", 0)

    @property
    def kind(self):
        return "installed" if self.installed else "source"

    def describe(self):
        return "%s (%s build %s, CL %s)" % (self.root, self.kind, self.version, self.changelist)

    def index_dir(self):
        if self.installed:
            return HOME_INDEX / ("%s-CL%s" % (self.version, self.changelist))
        return self.engine_dir / ".claude" / "ue-index"

    def git_head(self):
        return git_head(self.root)

    def marketplace_plugins(self):
        d = self.engine_dir / "Plugins" / "Marketplace"
        try:
            return sorted(e.name for e in os.scandir(d) if e.is_dir())
        except OSError:
            return []

    def fingerprint(self):
        fp = {
            "schema": SCHEMA,
            "build_version": self.build_version,
            "marketplace": hashlib.sha1("|".join(self.marketplace_plugins()).encode()).hexdigest()[:12],
        }
        if not self.installed:
            fp["git_head"] = self.git_head() or "none"
        return fp


def git_head(root):
    g = Path(root) / ".git"
    try:
        if g.is_file():
            txt = g.read_text().strip()
            if txt.startswith("gitdir:"):
                g = (Path(root) / txt[7:].strip()).resolve()
        head = (g / "HEAD").read_text().strip()
    except OSError:
        return None
    if not head.startswith("ref:"):
        return head
    ref = head[4:].strip()
    common = g
    cd = g / "commondir"
    if cd.is_file():
        common = (g / cd.read_text().strip()).resolve()
    for base in (g, common):
        p = base / ref
        if p.is_file():
            return p.read_text().strip()
    try:
        for line in (common / "packed-refs").read_text().splitlines():
            if line.endswith(" " + ref):
                return line.split(" ", 1)[0]
    except OSError:
        pass
    return None


def known_engines():
    """[(association_label, root_path)] for every engine the launcher/registry knows about."""
    found = []
    if os.name == "nt":
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\EpicGames\Unreal Engine") as k:
                i = 0
                while True:
                    try:
                        sub = winreg.EnumKey(k, i)
                    except OSError:
                        break
                    i += 1
                    try:
                        with winreg.OpenKey(k, sub) as sk:
                            found.append((sub, winreg.QueryValueEx(sk, "InstalledDirectory")[0]))
                    except OSError:
                        pass
        except OSError:
            pass
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"SOFTWARE\Epic Games\Unreal Engine\Builds") as k:
                i = 0
                while True:
                    try:
                        name, val, _ = winreg.EnumValue(k, i)
                    except OSError:
                        break
                    i += 1
                    found.append((name, val))
        except OSError:
            pass
    else:
        ini = Path(os.path.expanduser("~/.config/Epic/UnrealEngine/Install.ini"))
        try:
            in_sec = False
            for line in ini.read_text().splitlines():
                line = line.strip()
                if line.startswith("["):
                    in_sec = line == "[Installations]"
                elif in_sec and "=" in line:
                    k, v = line.split("=", 1)
                    found.append((k.strip(), v.strip()))
        except OSError:
            pass
        shared = Path("/Users/Shared/Epic Games")
        if shared.is_dir():
            for d in shared.iterdir():
                m = re.match(r"UE_(\d+\.\d+)$", d.name)
                if m:
                    found.append((m.group(1), str(d)))
    for root in (read_json(REGISTRY_FILE) or {}):
        found.append(("", root))
    out, seen = [], set()
    for label, path in found:
        n = norm(path)
        if n in seen or not (Path(path) / "Engine" / "Build" / "Build.version").is_file():
            continue
        seen.add(n)
        out.append((label, str(Path(path))))
    return out


def find_root_upwards(start):
    p = Path(start).resolve()
    for cand in [p] + list(p.parents):
        if (cand / "Engine" / "Build" / "Build.version").is_file():
            return cand
        if cand.name == "Engine" and (cand / "Build" / "Build.version").is_file():
            return cand.parent
    return None


def resolve_association(assoc, project_dir=None):
    engines = known_engines()
    for label, path in engines:
        if label and label.lower() == assoc.lower():
            return Engine(path, "EngineAssociation %s" % assoc)
    if project_dir:
        p = (Path(project_dir) / assoc)
        if p.exists() and find_root_upwards(p):
            return Engine(find_root_upwards(p), "EngineAssociation path %s" % assoc)
    m = re.match(r"^(\d+)\.(\d+)$", assoc)
    if m:
        # e.g. a project associated with "5.6" whose engine is a source build of 5.6.
        matches = []
        for _, path in engines:
            bv = read_json(Path(path) / "Engine" / "Build" / "Build.version") or {}
            if (str(bv.get("MajorVersion")), str(bv.get("MinorVersion"))) == (m.group(1), m.group(2)):
                matches.append(path)
        if matches:
            return Engine(matches[0], "inferred: only %s engine matching %s" % (
                "the" if len(matches) == 1 else "first of %d" % len(matches), assoc))
    raise EngineError("cannot resolve EngineAssociation '%s'. Known engines:\n%s" % (assoc, format_known(engines)))


def format_known(engines=None):
    engines = known_engines() if engines is None else engines
    if not engines:
        return "  (none found)"
    return "\n".join("  %-40s %s" % (label or "-", path) for label, path in engines)


def engine_from_project(uproject):
    uproject = Path(uproject).resolve()
    data = read_json(uproject)
    if data is None:
        raise EngineError("cannot read %s" % uproject)
    assoc = (data.get("EngineAssociation") or "").strip()
    if not assoc:
        root = find_root_upwards(uproject.parent)
        if root:
            return Engine(root, "project inside engine tree")
        raise EngineError("%s has no EngineAssociation and is not inside an engine tree" % uproject)
    return resolve_association(assoc, uproject.parent)


def find_engine(engine_arg=None, project_arg=None, cwd=None):
    if engine_arg:
        p = Path(engine_arg)
        if p.exists():
            root = find_root_upwards(p)
            if not root:
                raise EngineError("%s is not inside an Unreal Engine tree" % p)
            return Engine(root, "--engine")
        return resolve_association(engine_arg)
    if project_arg:
        return engine_from_project(project_arg)
    cwd = Path(cwd or os.getcwd()).resolve()
    root = find_root_upwards(cwd)
    if root:
        return Engine(root, "cwd is inside engine tree")
    for d in [cwd] + list(cwd.parents):
        try:
            projects = sorted(d.glob("*.uproject"))
        except OSError:
            projects = []
        if projects:
            return engine_from_project(projects[0])
    # A folder of several projects (like ~/Documents/Unreal): look one level down.
    try:
        projects = sorted(cwd.glob("*/*.uproject"))
    except OSError:
        projects = []
    assocs = {}
    for pr in projects:
        a = ((read_json(pr) or {}).get("EngineAssociation") or "").strip()
        if a:
            assocs.setdefault(a, pr)
    if len(assocs) == 1:
        return engine_from_project(next(iter(assocs.values())))
    hint = ""
    if len(assocs) > 1:
        hint = "\nProjects below cwd use several engines: %s" % ", ".join(sorted(assocs))
    raise EngineError("cannot tell which engine to use; pass --engine <path|association> or --project <file.uproject>.%s\nKnown engines:\n%s"
                      % (hint, format_known()))


# ---------------------------------------------------------------- status

def lock_path(engine):
    return engine.index_dir() / ".lock"


def read_lock(engine):
    lp = lock_path(engine)
    try:
        st = lp.stat()
    except OSError:
        return None
    age = time.time() - st.st_mtime
    info = ""
    try:
        info = lp.read_text().strip()
    except OSError:
        pass
    return {"age": age, "stale": age > LOCK_STALE_SECONDS, "info": info}


def status(engine):
    """Returns (state, detail). state in fresh|missing|stale|building."""
    d = engine.index_dir()
    lock = read_lock(engine)
    meta = read_json(d / "meta.json")
    if lock and not lock["stale"]:
        return "building", "build in progress for %dm (%s)" % (lock["age"] // 60, lock["info"])
    if not meta:
        return "missing", "no index at %s" % d
    cur = engine.fingerprint()
    old = meta.get("fingerprint", {})
    diffs = [k for k in sorted(set(cur) | set(old)) if cur.get(k) != old.get(k)]
    if meta.get("engine_root") and norm(meta["engine_root"]) != norm(engine.root) and engine.installed:
        diffs.append("engine_root")
    if diffs:
        return "stale", "changed: %s" % ", ".join(diffs)
    return "fresh", "built %s" % meta.get("built_at", "?")


def register(engine):
    reg = read_json(REGISTRY_FILE) or {}
    reg[str(engine.root)] = {"index": str(engine.index_dir()), "kind": engine.kind, "version": engine.version}
    try:
        write_json(REGISTRY_FILE, reg)
    except OSError:
        pass


def is_tty():
    return hasattr(sys.stdout, "isatty") and sys.stdout.isatty()
