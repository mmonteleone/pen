#!/usr/bin/env bash
# Lists every check description from the given sections of tests/smoke.sh that's missing from the test
# classes in cli/Pen.SmokeTests, and exits non-zero if any is. Temporary: it goes with smoke.sh once every
# section has its classes.
#
#   tests/smoke-parity.sh 5            once section 5 is ported
#   tests/smoke-parity.sh 1 2 3 4 5    once sections 1 to 5 are
#
# Run it over every section ported so far, not only the latest ones: literals aren't tied to a section, so
# a description that several sections share (such as "nothing is left running") could otherwise be matched
# by a literal that belongs to a section left out of the run.
#
# A description is the first argument of a check call, as bash would expand it, normalized:
# - Backslash escapes in double quotes (\$, \", \\, \`) become the character itself.
# - ${name:+…}, text shown only when a check fails or has something to add, is dropped.
# - A for loop over a literal word list runs its checks once per value, whether or not they use its
#   variable, and the variable is expanded, in ${name:-…}, ${name-…}, ${name:+…} and the like too (for bad
#   in "--port 0" "--port http" gives "pen --port 0 …" and "pen --port http …").
# - A check inside a function counts once per call in the section, with the call's literal arguments
#   for $1, $2… and the locals set from them (prompt_check claude … gives "claude answers …").
# - Any other expansion ($name, ${name…}, $(…), `…`, $@) is a runtime value and matches any text, e.g.
#   an interpolation hole in the C# string.
# Each description must be a whole C# string literal of its own (its display name, or an assertion message)
# in a .cs file under cli/Pen.SmokeTests outside Harness/, with C# escapes and {{ }} read as C# does. Literals
# in comments and raw strings don't count. A description that a section checks n times needs n literals,
# each counted for one check only, so a dropped copy of a repeated description is missing too.
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
[ $# -gt 0 ] || { echo "usage: tests/smoke-parity.sh <section number>…" >&2; exit 2; }

exec python3 -I - "$REPO/tests/smoke.sh" "$REPO/cli/Pen.SmokeTests" "$@" <<'PY'
import itertools, os, re, shlex, sys

smoke, tests_dir, wanted = sys.argv[1], sys.argv[2], sys.argv[3:]
for n in wanted:
    if not n.isdigit():
        sys.exit(f"not a section number: {n}")

# Logical lines: continuation lines joined, each with its first line number.
lines, buf, start = [], "", None
for number, raw in enumerate(open(smoke, encoding="utf-8").read().split("\n"), 1):
    if start is None:
        start = number
    if raw.endswith("\\") and not raw.endswith("\\\\"):
        buf += raw[:-1] + " "
        continue
    lines.append((start, buf + raw))
    buf, start = "", None


class Dyn:
    """A runtime value: matches any text."""


def skip_quoted(s, i):
    """Index after the quoted string starting at s[i] (' or ")."""
    q = s[i]
    i += 1
    while i < len(s) and s[i] != q:
        i += 2 if q == '"' and s[i] == "\\" else 1
    return i + 1


def skip_balanced(s, i, open_, close):
    """Index after the bracket that closes the one at s[i], skipping quoted strings."""
    depth = 0
    while i < len(s):
        c = s[i]
        if c in "'\"":
            i = skip_quoted(s, i)
            continue
        if c == "\\":
            i += 2
            continue
        if c == open_:
            depth += 1
        elif c == close:
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return i


def parse_dollar(s, i, parts):
    """Parses the expansion at s[i] == '$'; returns the index after it."""
    nxt = s[i + 1] if i + 1 < len(s) else ""
    if nxt == "(":
        parts.append(Dyn())
        return skip_balanced(s, i + 1, "(", ")")
    if nxt == "{":
        end = skip_balanced(s, i + 1, "{", "}")
        inner = s[i + 2:end - 1]
        m = re.fullmatch(r"(\w+)", inner)
        op = re.fullmatch(r"([A-Za-z_]\w*)(:?[-+=])(.*)", inner, re.S)
        if m and inner.isdigit():
            parts.append(("pos", int(inner)))
        elif m:
            parts.append(("var", m.group(1)))
        elif op:
            parts.append(("op", op.group(1), op.group(2), parse_text(op.group(3))))
        elif not re.fullmatch(r"\w+:\+.*", inner, re.S):
            parts.append(Dyn())
        return end
    m = re.match(r"[A-Za-z_]\w*", s[i + 1:])
    if m:
        parts.append(("var", m.group(0)))
        return i + 1 + m.end()
    if nxt.isdigit():
        parts.append(("pos", int(nxt)))
        return i + 2
    if nxt and nxt in "@*#?$!-":
        parts.append(Dyn())
        return i + 2
    parts.append("$")
    return i + 1


def parse_text(s):
    """The parts of the word in ${name:-word} and the like, read as in double quotes; Dyn if it has quotes."""
    parts, i = [], 0
    while i < len(s):
        c = s[i]
        if c in "'\"":
            return [Dyn()]
        if c == "\\" and s[i + 1:i + 2] in ("$", "`", '"', "\\"):
            parts.append(s[i + 1])
            i += 2
        elif c == "$":
            i = parse_dollar(s, i, parts)
        elif c == "`":
            return [Dyn()]
        else:
            parts.append(c)
            i += 1
    return parts


def parse_word(s, i):
    """Parses the bash word at s[i]: a list of literal strings, ("var", name), ("pos", n) and Dyn()."""
    parts = []
    while i < len(s) and not s[i].isspace() and s[i] not in ";&|<>()":
        c = s[i]
        if c == "'":
            end = s.index("'", i + 1)
            parts.append(s[i + 1:end])
            i = end + 1
        elif c == '"':
            i += 1
            while s[i] != '"':
                c = s[i]
                if c == "\\" and s[i + 1] in '$`"\\':
                    parts.append(s[i + 1])
                    i += 2
                elif c == "$":
                    i = parse_dollar(s, i, parts)
                elif c == "`":
                    parts.append(Dyn())
                    i = s.index("`", i + 1) + 1
                else:
                    parts.append(c)
                    i += 1
            i += 1
        elif c == "\\":
            parts.append(s[i + 1])
            i += 2
        elif c == "$":
            i = parse_dollar(s, i, parts)
        else:
            parts.append(c)
            i += 1
    return parts


def loop_values(words):
    """The values of a for loop's word list, or None if they're computed at run time."""
    values = []
    for token in shlex.split(words, posix=False):
        if len(token) > 1 and token[0] == token[-1] == "'":
            values.append(token[1:-1])
        elif len(token) > 1 and token[0] == token[-1] == '"' and not re.search(r"[$`\\]", token):
            values.append(token[1:-1])
        elif not re.search(r"[$`*?\[\\'\"]", token):
            values.append(token)
        else:
            return None
    return values


LOOP = re.compile(r"^\s*(?:for\s+(\w+)\s+in\s+(.*?)|while\b.*?|until\b.*?)\s*;\s*do\b(.*)$")
PREFIX = r"(?:^\s*|[;&|(]\s*|\bthen\s+|\bdo\s+|\belse\s+)"
CHECK = re.compile(PREFIX + r"check\s+(?=\S)")
FUNCTION = re.compile(r"^(\s*)(?:function\s+)?(\w+)\s*\(\)\s*\{\s*(?:#.*)?$")
LOCAL = re.compile(r"(?:^|[\s;])(?:local\s+)?(\w+)=\$\{?(\d)\}?(?=[\s;]|$)")
SHIFT = re.compile(r"(?:^|[\s;])shift(?:\s+(\d+))?(?=[\s;]|$)")


def parse_args(s, i):
    """The words of a command's arguments from s[i], up to the end of the command."""
    words = []
    while True:
        while i < len(s) and s[i] in " \t":
            i += 1
        if i >= len(s) or s[i] in ";&|<>()#":
            return words
        word = parse_word(s, i)
        words.append(word)
        # parse_word doesn't say where it stopped: skip the word the same way it reads it.
        while i < len(s) and not s[i].isspace() and s[i] not in ";&|<>()":
            if s[i] in "'\"":
                i = skip_quoted(s, i)
            elif s[i] == "\\":
                i += 2
            elif s[i] == "$" and i + 1 < len(s) and s[i + 1] in "({":
                i = skip_balanced(s, i + 1, s[i + 1], ")" if s[i + 1] == "(" else "}")
            else:
                i += 1


class Function:
    def __init__(self, name, indent, number):
        self.name, self.indent, self.number = name, indent, number
        self.checks, self.aliases, self.shift, self.loops = [], {}, 0, []


def track_loops(line, loops):
    """The loop scope for a line's checks, updating the open loops for the lines after it."""
    m = LOOP.match(line)
    if not m:
        if re.match(r"^\s*done\b", line) and loops:
            loops.pop()
        return dict(loops)
    frame = (m.group(1), loop_values(m.group(2)) if m.group(1) else None)
    if not re.search(r"(?:^|;)\s*done\b", m.group(3)):
        loops.append(frame)
        return dict(loops)
    return dict(loops + [frame])


sections, functions = {}, {}
section, loops, current = None, [], None
for number, line in lines:
    if current is not None:
        if re.fullmatch(re.escape(current.indent) + r"\}\s*(?:#.*)?", line):
            if current.checks:
                functions[current.name] = current
            current = None
            continue
        if line.lstrip().startswith("#"):
            continue
        for name, n in LOCAL.findall(line):
            current.aliases[name] = int(n) + current.shift
        scope = track_loops(line, current.loops)
        for found in CHECK.finditer(line):
            parts = [("pos", p[1] + current.shift) if isinstance(p, tuple) and p[0] == "pos" else p
                     for p in parse_word(line, found.end())]
            parts = [("pos", current.aliases[p[1]]) if isinstance(p, tuple) and p[0] == "var"
                     and p[1] in current.aliases else p for p in parts]
            current.checks.append((number, parts, scope))
        for shift in SHIFT.findall(line):
            current.shift += int(shift or 1)
        continue
    m = re.match(r'^section "(\d+)\. ', line)
    if m:
        section, loops = m.group(1), []
        continue
    if line.lstrip().startswith("#"):
        continue
    m = FUNCTION.match(line)
    if m:
        current = Function(m.group(2), m.group(1), number)
        continue
    if section is None:
        continue
    scope = track_loops(line, loops)
    for found in CHECK.finditer(line):
        sections.setdefault(section, []).append((f"smoke.sh:{number}", parse_word(line, found.end()), scope))
    for name, function in functions.items():
        for found in re.finditer(PREFIX + re.escape(name) + r"(?=\s|;|$)", line):
            args = parse_args(line, found.end())
            for check_number, parts, inner in function.checks:
                called = []
                for p in parts:
                    if isinstance(p, tuple) and p[0] == "pos":
                        called.extend(args[p[1] - 1] if 0 < p[1] <= len(args) else [Dyn()])
                    else:
                        called.append(p)
                where = f"smoke.sh:{number}, {name} at smoke.sh:{check_number}"
                sections.setdefault(section, []).append((where, called, {**scope, **inner}))


def expansions(parts, scope):
    """Every description a check prints, once per run of the loops around it that have literal word lists
    (whether or not it uses their variables): their variables expanded, the rest Dyn."""
    names = sorted(name for name, values in scope.items() if name is not None and values is not None)
    for values in itertools.product(*(scope[n] for n in names)):
        yield resolve(parts, dict(zip(names, values)))


def resolve(parts, env):
    """The parts with the loop variables in env expanded, ${name:-…}, ${name-…}, ${name:+…} etc. included."""
    out = []
    for p in parts:
        if isinstance(p, tuple) and p[0] == "op":
            _, name, op, word = p
            if name not in env:
                # Text shown only when something is set is dropped; any other use is a runtime value.
                if op != ":+":
                    out.append(Dyn())
                continue
            value = env[name]
            if op in ("-", "="):
                out.append(value)
            elif op in (":-", ":="):
                out.extend([value] if value else resolve(word, env))
            elif op == "+" or value:
                out.extend(resolve(word, env))
        elif isinstance(p, tuple):
            out.append(env[p[1]] if p[1] in env else Dyn())
        else:
            out.append(p)
    return out


def pieces(parts):
    """The literal text between the runtime values."""
    text = "".join("\0" if isinstance(p, Dyn) else p for p in parts)
    return re.sub("\0+", "\0", text).split("\0")


def shown(parts):
    return "".join("…" if isinstance(p, Dyn) else p for p in parts)


# C# string literals, as the compiler reads them, outside comments. Raw strings are skipped: they're
# in-sandbox scripts, not descriptions. An interpolation hole keeps its source text, e.g. {dir}, which
# only a runtime value in a description matches.
LITERAL_START = re.compile(r'(\$+@?|@\$*)?("{3,}|")')
ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "0": "\0", "\\": "\\", '"': '"', "'": "'"}


def skip_char(s, i):
    j = i + 1
    if j < len(s) and s[j] == "\\":
        j += 2
    while j < len(s) and s[j] != "'":
        j += 1
    return j + 1


def skip_hole(s, i):
    """Index after the interpolation hole whose { is at s[i]."""
    depth, i = 0, i
    while i < len(s):
        c = s[i]
        m = LITERAL_START.match(s, i) if c in '$@"' else None
        if m:
            i, _ = read_literal(s, i, m)
            continue
        if c == "'":
            i = skip_char(s, i)
            continue
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return i


def read_literal(s, i, m):
    """(index after the literal, its text or None for a raw string) for the literal matched by m at s[i]."""
    prefix, quotes = m.group(1) or "", m.group(2)
    i = m.end()
    if len(quotes) >= 3:
        return s.index(quotes, i) + len(quotes), None
    verbatim, interpolated = "@" in prefix, "$" in prefix
    out = []
    while i < len(s):
        c = s[i]
        if c == '"':
            if verbatim and s[i + 1:i + 2] == '"':
                out.append('"')
                i += 2
                continue
            return i + 1, "".join(out)
        if c == "\\" and not verbatim:
            out.append(ESCAPES.get(s[i + 1], "\\" + s[i + 1]))
            i += 2
        elif interpolated and c in "{}" and s[i + 1:i + 2] == c:
            out.append(c)
            i += 2
        elif interpolated and c == "{":
            end = skip_hole(s, i)
            out.append(s[i:end])
            i = end
        else:
            out.append(c)
            i += 1
    return i, "".join(out)


def literals(path):
    s = open(path, encoding="utf-8").read()
    i = 0
    while i < len(s):
        c = s[i]
        if s.startswith("//", i):
            i = s.find("\n", i)
            i = len(s) if i < 0 else i
        elif s.startswith("/*", i):
            i = s.index("*/", i + 2) + 2
        elif c == "'":
            i = skip_char(s, i)
        elif c in '$@"' and (m := LITERAL_START.match(s, i)):
            line = s.count("\n", 0, i) + 1
            i, text = read_literal(s, i, m)
            if text is not None:
                yield line, text
        elif c.isalnum() or c == "_":
            while i < len(s) and (s[i].isalnum() or s[i] == "_"):
                i += 1
        else:
            i += 1


found = []
for root, dirs, files in os.walk(tests_dir):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj", "Harness")]
    for name in sorted(files):
        if name.endswith(".cs"):
            path = os.path.join(root, name)
            found.extend((os.path.relpath(path, tests_dir), line, text) for line, text in literals(path))

wanted_checks = []
for n in wanted:
    if n not in sections:
        sys.exit(f"tests/smoke.sh has no checks in a section {n}")
    for where, parts, scope in sections[n]:
        for description in expansions(parts, scope):
            wanted_checks.append((n, where, description))

# Which literals each description could be: the whole literal, runtime values matching any text.
candidates, cache = [], {}
for n, where, description in wanted_checks:
    key = tuple(pieces(description))
    if key not in cache:
        regex = re.compile(".*?".join(re.escape(piece) for piece in key), re.S)
        longest = max(key, key=len)
        cache[key] = [j for j, (_, _, text) in enumerate(found) if longest in text and regex.fullmatch(text)]
    candidates.append(cache[key])

# Each literal counts for one check: a maximum bipartite matching (augmenting paths).
sys.setrecursionlimit(max(1000, 4 * len(wanted_checks) + 100))
owner = {}


def assign(k, seen):
    for j in candidates[k]:
        if j not in seen:
            seen.add(j)
            if j not in owner or assign(owner[j], seen):
                owner[j] = k
                return True
    return False


missing = []
for k, (n, where, description) in enumerate(wanted_checks):
    if not assign(k, set()):
        wanted_here = sum(1 for d in wanted_checks if tuple(pieces(d[2])) == tuple(pieces(description)))
        missing.append(f"  §{n} ({where}): {shown(description)}"
                       f" [{wanted_here} wanted, {len(candidates[k])} matching literals]")

if missing:
    print(f"missing from cli/Pen.SmokeTests ({len(missing)} of {len(wanted_checks)}):")
    print("\n".join(missing))
    sys.exit(1)
print(f"nothing missing: {len(wanted_checks)} descriptions from section(s) {' '.join(wanted)}")
PY
