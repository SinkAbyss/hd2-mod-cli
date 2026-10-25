"""hd2qd's Lua 5.1 syntax check against PUC Lua 5.1 itself (lupa's lua51).

    python tests/lua_syntax_vs_puc.py HD2QD_EXE WORK_DIR [MOD_FOLDER ...] [--mutants N] [--seed S]

Every plain-text Lua script in the patch archives under the mod folders, plus the
snippets below, plus N altered copies of them (tokens dropped, LuaJIT and Lua 5.2+
syntax put in, cut short, line breaks moved), is written to WORK_DIR and compiled
twice: by PUC Lua 5.1 (loadstring) and by `hd2qd lua-syntax`. The two must agree on
every file, and on the line when both refuse it. Refusals for the code generator's
limits (registers, locals, upvalues, nesting), which hd2qd does not check, are
counted apart. Needs `pip install lupa`.
"""
import locale
import os
import random
import re
import struct
import subprocess
import sys

locale.setlocale(locale.LC_CTYPE, "C")      # the game's C runtime: only ASCII letters are letters
import lupa.lua51 as lupa                    # noqa: E402

LUA_TYPE = 0xA14E8DFA2CD117E2
LIMITS = ("too complex", "constant table overflow", "control structure too long",
          "variables in assignment")       # the code generator's; hd2qd checks locals, upvalues, nesting

SNIPPETS = [
    "local a = 1", "goto done ::done::", "local x = 0xffull", "local x = 1LL", "a = b // c", "a = b & c",
    "local t = {[[a]], [==[b]==]}", "local s = [[ [[nested]] ]]", "local s = [=[ [[ok]] ]=]",
    "f\n(x)", "local f = g\n(h)()", "x = 1; return; y = 2", "do return end", "return", "break",
    "while true do break end", "for i = 1, 2 do end", "for k, v in pairs(t) do end", "for i do end",
    "function f(...) return ... end", "function f() return ... end", "local function f(a, ...) end",
    "t = {a = 1, b = 2; 3, [4] = 5,}", "t = {x\n(y)}", "a.b.c:d(e)", "(f)()", "(a) = 1", "f() = 1",
    "a, b = 1, 2", "a, f() = 1, 2", "local a <const> = 1", "x = 1.5e3 + .5 + 3. + 0x1F", "x = 1..2",
    "x = 1e", "x = 0x", "x = '\\300'", "x = '\\255'", "x = 'a\nb'", "x = 'a\\\nb'", "x = '\\z'",
    "--[==[ comment ]==] x = 1", "--[[ [[ ]] ]] x = 1", "--[= x = 1", "x = [=", "if a then elseif b then else end",
    "repeat local x until x", "x = not not a", "x = #t", "x = -2^-3", "x = a or b and c", "x = a ~= b",
    "x = ~a", "local function", "function a.b:c() end", "function a:b.c() end", "﻿x = 1", "x = 1 -- é",
    "x = 'é'", "é = 1", "x = 1\r\ny = 2", "x = 'a'\r\n(f)()", "#!/bin/lua\nx = 1", "x = 1 y = 2",
    "local x, y", "local x =", "x = {", "x = function() end end", "if x then", "f{1}{2}'s'", "a.b = 1 + 2 * 3",
]

TOKENS = ["goto x", "::x::", "0xfeedull", "1LL", "//", "&", "|", "<<", "~", "end", "(", ")", "{", "}", "[[", "]]",
          "=", "==", ",", ";", "local", "function", "return", "break", "...", "\n(", "'", '"', "--[[", "\\", "@", "0x", "1e"]


def lua_texts(folder):
    """The plain-text Lua scripts in every patch archive under a folder."""
    for root, _, files in os.walk(folder):
        for f in files:
            if not re.search(r"\.patch_\d+$", f):
                continue
            path = os.path.join(root, f)
            with open(path, "rb") as h:
                head = h.read(0x48)
                if len(head) < 0x48 or struct.unpack_from("<I", head)[0] != 0xF0000011:
                    continue
                types, count = struct.unpack_from("<II", head, 4)
                h.seek(0x48 + types * 32)
                rows = h.read(count * 80)
                for i in range(0, len(rows) - 79, 80):
                    rid, rtype, off = struct.unpack_from("<QQQ", rows, i)
                    size = struct.unpack_from("<I", rows, i + 0x38)[0]
                    if rtype != LUA_TYPE or size <= 8:
                        continue
                    h.seek(off)
                    data = h.read(size)
                    n, kind = struct.unpack_from("<II", data, 0)
                    body = data[8:8 + n]
                    if kind == 2 and body[:1] != b"\x1b":
                        yield "%016x" % rid, body


def mutate(rng, text):
    lines = text.split(b"\n")
    how = rng.randrange(6)
    if how == 0 and len(text) > 1:                        # cut short
        return text[:rng.randrange(len(text))]
    if how == 1:                                          # a token put in
        at = rng.randrange(len(text) + 1)
        return text[:at] + (" " + rng.choice(TOKENS) + " ").encode() + text[at:]
    if how == 2 and len(text) > 2:                        # a few bytes dropped
        at = rng.randrange(len(text) - 1)
        return text[:at] + text[at + rng.randrange(1, min(8, len(text) - at) + 1):]
    if how == 3 and len(lines) > 1:                       # a line dropped
        del lines[rng.randrange(len(lines))]
        return b"\n".join(lines)
    if how == 4:                                          # a line break before a bracket
        spots = [m.start() for m in re.finditer(rb"\(", text)]
        if spots:
            at = rng.choice(spots)
            return text[:at] + b"\n" + text[at:]
    at = rng.randrange(len(text) + 1)                     # a line repeated somewhere else
    return text[:at] + b"\n" + rng.choice(lines) + b"\n" + text[at:]


def main():
    args = sys.argv[1:]
    mutants, seed = 2000, 1
    if "--mutants" in args:
        i = args.index("--mutants"); mutants = int(args[i + 1]); del args[i:i + 2]
    if "--seed" in args:
        i = args.index("--seed"); seed = int(args[i + 1]); del args[i:i + 2]
    exe, work, folders = os.path.abspath(args[0]), args[1], args[2:]
    os.makedirs(work, exist_ok=True)
    for old in os.listdir(work):
        if old.endswith(".lua"):
            os.remove(os.path.join(work, old))
    samples = [("snippet%03d" % i, s.encode("utf-8")) for i, s in enumerate(SNIPPETS)]
    seen = set()
    for folder in folders:
        for rid, body in lua_texts(folder):
            if body not in seen:
                seen.add(body)
                samples.append(("mod_" + rid, body))
    print("%d snippets, %d scripts from the mods" % (len(SNIPPETS), len(samples) - len(SNIPPETS)))
    rng = random.Random(seed)
    small = [s for s in samples if len(s[1]) < 200000] or samples
    for k in range(mutants):
        name, body = rng.choice(small)
        samples.append(("mut%05d_%s" % (k, name), mutate(rng, body)))

    lua = lupa.LuaRuntime(encoding=None)        # messages as bytes: a broken script can make them invalid UTF-8
    loadstring = lua.eval(b"loadstring")
    puc = {}
    for name, body in samples:
        path = os.path.join(work, name + ".lua")
        with open(path, "wb") as h:
            h.write(body)
        r = loadstring(body)
        if isinstance(r, tuple) and r[0] is None:
            msg = r[1].decode("utf-8", "replace") if isinstance(r[1], bytes) else str(r[1])
            # the chunk's name may hold quotes, or be the text itself (a script starting with "=")
            m = re.search(r'"\]:(\d+): (.*)', msg, re.S) or re.search(r':(\d+): (.*)', msg, re.S)
            puc[path] = ("ERROR", int(m.group(1)) if m else 0, msg)
        else:
            puc[path] = ("OK", 0, "")

    ours = {}
    paths = list(puc)
    for i in range(0, len(paths), 200):
        out = subprocess.run([exe, "lua-syntax"] + paths[i:i + 200], capture_output=True, text=True, encoding="utf-8").stdout
        for line in out.split("\n"):              # not splitlines: a message may hold \x85
            if "\t" not in line:
                continue
            p = line.split("\t")
            ours[p[1]] = (p[0], int(p[2]) if p[0] == "ERROR" else 0, p[3] if len(p) > 3 else "")

    agree = limits = 0
    differ = []
    for path, (verdict, line, msg) in puc.items():
        mine = ours.get(path, ("MISSING", 0, ""))
        if verdict == "ERROR" and any(k in msg for k in LIMITS):
            limits += 1
            continue
        if mine[0] == verdict and (verdict == "OK" or mine[1] == line):
            agree += 1
        else:
            differ.append((os.path.basename(path), verdict, line, msg[:120], mine[0], mine[1], mine[2][:120]))
    print("%d files: %d agree, %d differ, %d refused by PUC for limits not checked here" % (len(puc), agree, len(differ), limits))
    for d in differ[:40]:
        print("  %s\n    PUC 5.1: %s line %s %s\n    hd2qd:   %s line %s %s" % d)
    sys.exit(1 if differ else 0)


if __name__ == "__main__":
    main()
