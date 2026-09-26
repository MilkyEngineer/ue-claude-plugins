"""Regex-based extraction of Unreal-specific declarations from C++ sources.

This is deliberately heuristic: it is tuned for UE coding conventions (UHT macros, *_API exports,
F/U/A/E/T/I prefixes) and favours cheap, robust matching over full C++ parsing. Every record carries
file:line so the agent can confirm against the real source.
"""
import bisect
import re

# ------------------------------------------------------------------ lexing

_TOKEN = re.compile(r'//[^\n]*|/\*.*?\*/|"(?:\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])*\'', re.S)
_NOT_NL = re.compile(r"[^\n]")


def _blank(s):
    return _NOT_NL.sub(" ", s)


def lex(text):
    """Returns (code, bare): comments blanked in both; string/char literal contents also blanked in `bare`.
    Both are the same length as `text`, so offsets and line numbers line up."""
    code, bare, pos = [], [], 0
    for m in _TOKEN.finditer(text):
        s = m.group()
        pre = text[pos:m.start()]
        code.append(pre)
        bare.append(pre)
        if s[0] == "/":
            b = _blank(s)
            code.append(b)
            bare.append(b)
        else:
            code.append(s)
            bare.append(s[0] + _blank(s[1:-1]) + s[-1] if len(s) >= 2 else s)
        pos = m.end()
    code.append(text[pos:])
    bare.append(text[pos:])
    return "".join(code), "".join(bare)


class Lines:
    def __init__(self, text):
        self.nl = [m.start() for m in re.finditer("\n", text)]

    def __call__(self, pos):
        return bisect.bisect_left(self.nl, pos) + 1


def match_braces(bare):
    """{open_index: close_index} for every '{' in `bare`."""
    out, stack = {}, []
    for m in re.finditer(r"[{}]", bare):
        if m.group() == "{":
            stack.append(m.start())
        elif stack:
            out[stack.pop()] = m.start()
    end = len(bare)
    for o in stack:
        out[o] = end
    return out


def balanced(bare, i, open_ch="(", close_ch=")"):
    """Index just past the bracket group starting at bare[i] == open_ch."""
    depth = 0
    n = len(bare)
    j = i
    while j < n:
        c = bare[j]
        if c == open_ch:
            depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0:
                return j + 1
        j += 1
    return n


def decl_end(bare, i, limit=2000, stop=";{"):
    """Index of the first `stop` char at paren/bracket depth 0 from i."""
    depth = 0
    n = min(len(bare), i + limit)
    j = i
    while j < n:
        c = bare[j]
        if c in "([":
            depth += 1
        elif c in ")]":
            depth -= 1
        elif depth <= 0 and c in stop:
            return j
        j += 1
    return n


_WS = re.compile(r"\s+")


def squash(s, maxlen=None):
    s = _WS.sub(" ", s).strip().replace("\t", " ")
    if maxlen and len(s) > maxlen:
        s = s[: maxlen - 1] + "~"
    return s


# ------------------------------------------------------------------ patterns

_PAREN = r"(?:[^()]|\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\))*"

TYPE_RE = re.compile(r"""
    (?:\b(?P<umacro>UCLASS|USTRUCT|UENUM|UINTERFACE)\s*\((?P<uspec>""" + _PAREN + r""")\)\s*)?
    (?:template\s*<[^;{}]*?>\s*)?
    \b(?P<kw>class|struct|union|enum\s+class|enum\s+struct|enum)\s+
    (?P<attrs>(?:(?:alignas|UE_DEPRECATED\w*|DEPRECATED|MS_ALIGN|GCC_ALIGN|UE_EXPERIMENTAL|UE_INTERNAL)\s*\((?:[^()]|\([^()]*\))*\)\s*
               |\[\[[^\]]*\]\]\s*
               |\w+_API\s+)*)
    (?P<name>[A-Za-z_]\w*)
    (?P<final>\s+final)?
    \s*(?::(?P<bases>[^;{}]*?))?
    \{""", re.X)

NS_ENUM_RE = re.compile(r"\bnamespace\s+(?P<name>[A-Za-z_]\w*)\s*\{\s*enum\s+Type\b")
NAMESPACE_RE = re.compile(r"\bnamespace\s+(?P<name>[A-Za-z_][\w:]*)?\s*\{")
UFUNC_RE = re.compile(r"\bUFUNCTION\s*\((?P<spec>" + _PAREN + r")\)")
UPROP_RE = re.compile(r"\bUPROPERTY\s*\((?P<spec>" + _PAREN + r")\)")
DELEGATE_RE = re.compile(r"\b(?P<macro>DECLARE_(?:DYNAMIC_)?(?:MULTICAST_)?(?:SPARSE_)?(?:TS_)?(?:DELEGATE|EVENT|DERIVED_EVENT)\w*)\s*\(")
ALIAS_RE = re.compile(r"\b(?:typedef\s+(?P<t1>[^;{}]+?)\s+(?P<n1>[FTUAEI][A-Z]\w*)\s*;|using\s+(?P<n2>[FTUAEI][A-Z]\w*)\s*=\s*(?P<t2>[^;{}]+);)")
API_FUNC_RE = re.compile(r"(?m)^[ \t]*(?:template\s*<[^;{}]*>\s*)?(?:(?:static|virtual|inline|FORCEINLINE|FORCENOINLINE|constexpr|explicit|friend)\s+)*(?P<api>\w+_API)\s+(?P<rest>[^;{}#]*?)\b(?P<name>~?[A-Za-z_]\w*|operator\s*[^\s(]+)\s*\(")
LOG_RE = re.compile(r"\b(?P<macro>(?:UE_)?(?:DECLARE_LOG_CATEGORY_EXTERN|DECLARE_LOG_CATEGORY_CLASS|DEFINE_LOG_CATEGORY_STATIC|DEFINE_LOG_CATEGORY_CLASS|DEFINE_LOG_CATEGORY))\s*\(\s*(?P<name>\w+)(?:\s*,\s*(?P<verb>\w+))?")
CVAR_RE = re.compile(r"""\b(?P<ctor>TAutoConsoleVariable\s*<(?P<type>[^>]*)>|FAutoConsoleVariableRef|FAutoConsoleVariable|FAutoConsoleCommand\w*)
    \s+(?P<var>\w+)\s*[({]\s*(?:TEXT\s*\(\s*)?"(?P<name>[^"]+)\"""", re.X)
CVAR_REG_RE = re.compile(r"""\bRegisterConsole(?P<which>Variable\w*|Command)\s*\(\s*(?:TEXT\s*\(\s*)?"(?P<name>[^"]+)\"""")
CONFIG_READ_RE = re.compile(r"""\bGConfig\s*->\s*(?P<fn>(?:Get|Set)\w*)\s*\(\s*TEXT\s*\(\s*"(?P<sec>[^"]+)"\s*\)\s*,\s*TEXT\s*\(\s*"(?P<key>[^"]+)"\s*\)""")
GTAG_RE = re.compile(r"""\bUE_DEFINE_GAMEPLAY_TAG\w*\s*\(\s*(?P<var>\w+)\s*,\s*(?:TEXT\s*\(\s*)?"(?P<tag>[^"]+)\"""")
DEPR_RE = re.compile(r"\bUE_DEPRECATED(?:_FORGAME|_FORENGINE)?\s*\(\s*(?P<ver>[\d.]+|all)\s*,")
STRLIT_RE = re.compile(r'"((?:\\.|[^"\\])*)"')
INI_SEC_RE = re.compile(r"^\s*\[(?P<sec>[^\]]+)\]\s*$")
INI_KEY_RE = re.compile(r"^\s*(?P<op>[+\-.!@*]?)(?P<key>[A-Za-z_][\w.:\[\]()\-]*)\s*=\s*(?P<val>.*)$")

CPP_QUICK = ("ConsoleVariable", "ConsoleCommand", "LOG_CATEGORY", "GConfig", "GAMEPLAY_TAG", "RegisterConsole")

_ACCESS = re.compile(r"\b(public|protected|private|virtual)\b")
_MACROISH = re.compile(r"^[A-Z][A-Z0-9_]*$")
_DECL_NOISE = re.compile(r"\b(?:virtual|static|inline|FORCEINLINE|FORCENOINLINE|explicit|constexpr|mutable|\w+_API|UE_NODISCARD|\[\[nodiscard\]\])\s+")
_DEPR_MACRO = re.compile(r"\bUE_DEPRECATED\w*\s*\((?:[^()]|\([^()]*\))*\)\s*")


def clean_bases(b):
    if not b:
        return ""
    return squash(_ACCESS.sub("", b), 100)


def compact_spec(spec, maxlen=110):
    return squash(spec, maxlen)


def split_args(s):
    out, depth, cur = [], 0, []
    for c in s:
        if c in "(<[{":
            depth += 1
        elif c in ")>]}":
            depth -= 1
        if c == "," and depth == 0:
            out.append("".join(cur).strip())
            cur = []
        else:
            cur.append(c)
    if "".join(cur).strip():
        out.append("".join(cur).strip())
    return out


def func_name_and_sig(decl):
    """decl: squashed declaration text up to ';' or '{'. Returns (name, signature) or (None, None)."""
    for m in re.finditer(r"(~?[A-Za-z_]\w*)\s*\(", decl):
        n = m.group(1)
        if n in ("decltype", "alignas", "sizeof", "TEXT") or (_MACROISH.match(n) and "_" in n) or n.startswith("UE_"):
            continue
        ret = decl[: m.start()]
        ret = _DEPR_MACRO.sub("", ret)
        ret = _DECL_NOISE.sub("", ret + " ").strip()
        end = balanced(decl, m.end() - 1)
        params = decl[m.end(): end - 1].strip()
        tail = decl[end:]
        sig = "%s (%s)" % (ret, squash(params))
        if re.search(r"\bconst\b", tail):
            sig += " const"
        if re.search(r"=\s*0", tail):
            sig += " =0"
        return n, squash(sig, 180)
    return None, None


def prop_name_and_type(decl):
    d = _DEPR_MACRO.sub("", decl)
    d = re.sub(r"\{.*\}\s*$", "", d)
    depth = 0
    for i, c in enumerate(d):
        if c in "<([":
            depth += 1
        elif c in ">)]":
            depth -= 1
        elif c == "=" and depth == 0:
            d = d[:i]
            break
    d = re.sub(r":\s*\d+\s*$", "", d.strip())
    d = re.sub(r"(\[[^\]]*\])+\s*$", "", d.strip())
    m = re.search(r"([A-Za-z_]\w*)\s*$", d)
    if not m:
        return None, None
    typ = _DECL_NOISE.sub("", d[: m.start()] + " ").strip()
    return m.group(1), squash(typ, 80)


# ------------------------------------------------------------------ file parsing

class FileResult:
    __slots__ = ("symbols", "funcs", "logs", "cvars", "config", "depr", "uclass", "cfgprops")

    def __init__(self):
        self.symbols = []   # [name, kind, line, detail]
        self.funcs = []     # [name, line, sig]
        self.logs = []      # [name, kind, line, verbosity]
        self.cvars = []     # [name, kind, line, type, flags, help]
        self.config = []    # [section, key, source, line, detail]
        self.depr = []      # [name, since, line, message]
        self.uclass = []    # [name, config, defaultconfig(0/1), firstbase]
        self.cfgprops = []  # [class, prop, type, line]


def _scopes(bare, braces):
    """List of (open, close, name, is_type) for namespace and type bodies."""
    spans = []
    for m in NAMESPACE_RE.finditer(bare):
        o = m.end() - 1
        spans.append((o, braces.get(o, len(bare)), m.group("name") or "", False))
    return spans


def _enclosing(spans, pos):
    """(type_name or None, namespace string) for the innermost scopes around pos."""
    inner_type, inner_open, ns = None, -1, []
    for o, c, name, is_type in spans:
        if o < pos < c:
            if is_type:
                if o > inner_open:
                    inner_type, inner_open = name, o
            elif name:
                ns.append((o, name))
    ns.sort()
    return inner_type, "::".join(n for _, n in ns)


def parse_source(text, is_header, module_name):
    r = FileResult()
    if not is_header and not any(k in text for k in CPP_QUICK) and "class " not in text and "struct " not in text:
        return r
    code, bare = lex(text)
    line_of = Lines(text)
    braces = match_braces(bare)
    spans = _scopes(bare, braces)

    # ---- types
    types = []
    for m in TYPE_RE.finditer(bare):
        name = m.group("name")
        if name in ("final", "alignas") or (_MACROISH.match(name) and "_" in name):
            continue
        o = m.end() - 1
        c = braces.get(o, len(bare))
        types.append((m, name, o, c))
        spans.append((o, c, name, True))
    for m in NS_ENUM_RE.finditer(bare):
        # UENUM() namespace EFoo { enum Type { ... } }
        head = bare[max(0, m.start() - 200): m.start()]
        is_u = re.search(r"\bUENUM\s*\(" + _PAREN + r"\)\s*$", head) is not None
        r.symbols.append([m.group("name"), "uenum" if is_u else "enum", line_of(m.start()), "namespace-enum"])

    for m, name, o, c in types:
        start = m.start("kw") if not m.group("umacro") else m.start()
        outer_type, ns = _enclosing([s for s in spans if s[0] != o], o)
        kw = m.group("kw").split()[0]
        umacro = m.group("umacro")
        kind = {"UCLASS": "uclass", "USTRUCT": "ustruct", "UENUM": "uenum", "UINTERFACE": "uiface"}.get(umacro, kw)
        if not is_header and kind in ("class", "struct", "union"):
            kind = "cpp-" + kind
        qual = "%s::%s" % (outer_type, name) if outer_type else name
        parts = []
        bases = clean_bases(m.group("bases"))
        if bases:
            parts.append(": " + bases)
        if umacro:
            spec = compact_spec(code[m.start("uspec"): m.end("uspec")])
            if spec:
                parts.append(spec)
        if "DEPRECATED" in (m.group("attrs") or ""):
            dm = re.search(r"DEPRECATED\w*\s*\(\s*([\d.]+)", code[m.start("attrs"): m.end("attrs")])
            parts.append("DEPRECATED" + ("(%s)" % dm.group(1) if dm else ""))
        if ns:
            parts.append("ns " + ns)
        r.symbols.append([qual, kind, line_of(m.start("name")), " | ".join(parts)])
        if umacro == "UCLASS":
            spec = code[m.start("uspec"): m.end("uspec")]
            cm = re.search(r"\b(?:Config|config)\s*=\s*(\w+)", spec)
            first_base = split_args(bases)[0].split()[-1] if bases else ""
            r.uclass.append([name, cm.group(1) if cm else "", 1 if re.search(r"\bdefaultconfig\b", spec, re.I) else 0, first_base])

    # ---- UFUNCTION
    covered = []
    for m in UFUNC_RE.finditer(bare):
        e = decl_end(bare, m.end())
        decl = squash(code[m.end(): e])
        covered.append((m.end(), e))
        name, sig = func_name_and_sig(decl)
        if not name:
            continue
        owner, _ = _enclosing(spans, m.start())
        spec = compact_spec(code[m.start("spec"): m.end("spec")])
        dep = "DEPRECATED | " if ("DeprecatedFunction" in spec or "UE_DEPRECATED" in decl) else ""
        r.symbols.append(["%s::%s" % (owner, name) if owner else name, "ufunc", line_of(m.start()),
                          "%s%s | %s" % (dep, sig, spec) if spec else dep + sig])

    # ---- UPROPERTY
    for m in UPROP_RE.finditer(bare):
        e = decl_end(bare, m.end(), stop=";")
        decl = squash(code[m.end(): e])
        name, typ = prop_name_and_type(decl)
        if not name:
            continue
        owner, _ = _enclosing(spans, m.start())
        spec_raw = code[m.start("spec"): m.end("spec")]
        spec = compact_spec(spec_raw)
        ln = line_of(m.start())
        r.symbols.append(["%s::%s" % (owner, name) if owner else name, "uprop", ln, "%s | %s" % (typ, spec) if spec else typ])
        if owner and re.search(r"\b(?:Config|GlobalConfig)\b", spec_raw, re.I):
            r.cfgprops.append([owner, name, typ, ln])

    # ---- delegates
    for m in DELEGATE_RE.finditer(bare):
        e = balanced(bare, m.end() - 1)
        args = split_args(squash(code[m.end(): e - 1]))
        macro = m.group("macro")
        idx = 2 if "DERIVED_EVENT" in macro else 1 if "EVENT" in macro else 0
        if len(args) <= idx or not re.match(r"^\w+$", args[idx]):
            continue
        owner, _ = _enclosing(spans, m.start())
        name = args[idx]
        rest = ", ".join(args[idx + 1:])
        r.symbols.append(["%s::%s" % (owner, name) if owner else name, "delegate", line_of(m.start()),
                          squash("%s(%s)" % (macro, rest), 160)])

    if is_header:
        # ---- aliases
        for m in ALIAS_RE.finditer(bare):
            name = m.group("n1") or m.group("n2")
            target = code[m.start("t1"): m.end("t1")] if m.group("n1") else code[m.start("t2"): m.end("t2")]
            owner, _ = _enclosing(spans, m.start())
            r.symbols.append(["%s::%s" % (owner, name) if owner else name, "alias", line_of(m.start()), squash("= " + target, 120)])

        # ---- exported (non-UFUNCTION) functions
        cov = sorted(covered)
        for m in API_FUNC_RE.finditer(bare):
            p = m.start("name")
            k = bisect.bisect_right(cov, (p, 1 << 60)) - 1
            if k >= 0 and cov[k][0] <= p < cov[k][1]:
                continue
            e = decl_end(bare, m.start("name"))
            decl = squash(code[m.start("rest"): e])
            name, sig = func_name_and_sig(decl)
            if not name:
                continue
            if name == "operator":
                name = squash(m.group("name")).replace(" ", "")
            owner, ns = _enclosing(spans, m.start())
            qual = "%s::%s" % (owner, name) if owner else ("%s::%s" % (ns, name) if ns else name)
            stmt = max(bare.rfind(";", 0, m.start()), bare.rfind("}", 0, m.start()), bare.rfind("{", 0, m.start()))
            if "UE_DEPRECATED" in code[stmt + 1: m.start("name")]:
                sig = "DEPRECATED | " + sig
            r.funcs.append([qual, line_of(p), sig])

        # ---- deprecations (non-reflected declarations; types/UFUNCTIONs are flagged inline)
        for m in DEPR_RE.finditer(bare):
            close = balanced(bare, bare.index("(", m.start()))
            msgs = [s for s in STRLIT_RE.findall(code[m.end(): close])]
            msg = squash(" ".join(msgs).replace("\\n", " "), 160)
            e = decl_end(bare, close)
            decl = squash(code[close: e])
            um = re.match(r"\s*U(?:PROPERTY|FUNCTION)\s*\(", decl)
            if um:
                decl = decl[balanced(decl, um.end() - 1):].strip()
            stmt = max(bare.rfind(";", 0, m.start()), bare.rfind("}", 0, m.start()), bare.rfind("{", 0, m.start()))
            if re.search(r"\b(?:class|struct|enum)\s*(?:class\s*)?$", bare[stmt + 1: m.start()]):
                im = re.match(r"\s*(?:\w+_API\s+)?(\w+)", decl)
                name = im.group(1) if im else None
            else:
                name, _ = func_name_and_sig(decl)
            if not name:
                tm = re.search(r"\b(?:class|struct|enum(?:\s+class)?)\s+(?:\w+_API\s+)?(\w+)", decl)
                name = tm.group(1) if tm else None
            if not name:
                pn, _ = prop_name_and_type(decl)
                name = pn
            if not name:
                name = squash(decl, 60) or "?"
            owner, _ = _enclosing(spans, m.start())
            r.depr.append(["%s::%s" % (owner, name) if owner and owner != name else name, m.group("ver"), line_of(m.start()), msg])

    # ---- log categories
    for m in LOG_RE.finditer(bare):
        macro = m.group("macro").replace("UE_", "")
        kind = {"DECLARE_LOG_CATEGORY_EXTERN": "extern", "DECLARE_LOG_CATEGORY_CLASS": "class",
                "DEFINE_LOG_CATEGORY_STATIC": "static", "DEFINE_LOG_CATEGORY_CLASS": "define-class",
                "DEFINE_LOG_CATEGORY": "define"}[macro]
        if m.group("name") in ("CategoryName", "Name"):
            continue  # macro definitions themselves
        r.logs.append([m.group("name"), kind, line_of(m.start()), m.group("verb") or ""])

    # ---- console variables / commands
    for m in CVAR_RE.finditer(code):
        if bare[m.start()] == " ":
            continue
        ctor = m.group("ctor")
        kind = "cmd" if "Command" in ctor else "var"
        typ = squash(m.group("type") or ("ref" if "Ref" in ctor else ""))
        _cvar_record(r, code, bare, m, kind, typ, line_of)
    for m in CVAR_REG_RE.finditer(code):
        if bare[m.start()] == " ":
            continue
        kind = "cmd" if m.group("which") == "Command" else "var"
        _cvar_record(r, code, bare, m, kind, "registered", line_of)

    # ---- config reads in code
    for m in CONFIG_READ_RE.finditer(code):
        if bare[m.start()] == " ":
            continue
        e = decl_end(bare, m.end(), limit=400, stop=";")
        ini = re.search(r"\b(G\w*Ini)\b", code[m.end(): e])
        r.config.append([m.group("sec"), m.group("key"), "code", line_of(m.start()),
                         "%s %s" % (m.group("fn"), ini.group(1) if ini else "")])

    # ---- native gameplay tags
    for m in GTAG_RE.finditer(code):
        if bare[m.start()] == " ":
            continue
        r.symbols.append([m.group("tag"), "gtag", line_of(m.start()), m.group("var")])
    return r


def _cvar_record(r, code, bare, m, kind, typ, line_of):
    e = decl_end(bare, m.end(), limit=2500, stop=";")
    body = code[m.end(): e]
    lits = STRLIT_RE.findall(body)
    if typ in ("FString",) and lits:
        lits = lits[1:]
    help_ = squash(" ".join(lits).replace("\\n", " ").replace('\\"', "'"), 200)
    flags = sorted(set(f[5:] for f in re.findall(r"\bECVF_\w+", body) if f != "ECVF_Default"))
    r.cvars.append([m.group("name"), kind, line_of(m.start()), typ, ",".join(flags), help_])


def parse_ini(text):
    """[(section, key, line, value)]"""
    out, sec = [], ""
    for i, line in enumerate(text.splitlines(), 1):
        s = line.strip()
        if not s or s[0] in ";#":
            continue
        m = INI_SEC_RE.match(s)
        if m:
            sec = m.group("sec").strip()
            continue
        m = INI_KEY_RE.match(s)
        if m and sec:
            out.append((sec, m.group("op") + m.group("key"), i, squash(m.group("val"), 120)))
    return out


def parse_build_cs(text):
    text = re.sub(r"//[^\n]*", "", text)
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    deps = {"Public": [], "Private": []}
    for m in re.finditer(r"\b(Public|Private)DependencyModuleNames\s*\.\s*(?:AddRange|Add)\s*\((.*?)\)\s*;", text, re.S):
        for n in re.findall(r'"([^"]+)"', m.group(2)):
            if n not in deps[m.group(1)]:
                deps[m.group(1)].append(n)
    return deps


def read_text(path):
    with open(path, "rb") as f:
        raw = f.read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    if raw[:3] == b"\xef\xbb\xbf":
        raw = raw[3:]
    return raw.decode("utf-8", errors="replace")
