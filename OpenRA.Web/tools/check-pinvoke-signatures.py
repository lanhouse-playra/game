#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Checks that every DllImport of a native module calls its C function with the WebAssembly
signature that the C headers declare. On native platforms a mismatch (an int return for a void
function, a missing argument, an int64 for an int) is harmless; in WebAssembly the call traps with
"function signature mismatch", or calls a stub that aborts.

How: the .NET wasm build writes the C prototype it generates for each DllImport into
pinvoke-table.h. This script compiles a C file that takes the address of each of those functions
through the real headers, with the same compiler, and reads the function types clang lowered them
to from the object file (so struct returns, varargs and 64-bit types are lowered exactly as the
linker sees them). Both sides are compared as WebAssembly value types (i32/i64/f32/f64).

Mismatches fail the check (exit 1) unless the allowlist names them, with a reason (normally "not
called by OpenRA"). Functions the headers do not declare are listed only: the link turns them into
JavaScript stubs that abort when called (WasmAllowUndefinedSymbols).

usage: check-pinvoke-signatures.py --table pinvoke-table.h --module NAME --emcc EMCC
           [--allowlist FILE] [--cflag FLAG]... HEADER...
HEADER is an #include argument, for example "<SDL2/SDL.h>" or "\"/path/lua.h\"".
"""

import argparse
import os
import re
import subprocess
import sys
import tempfile

VALTYPES = {0x7F: "i32", 0x7E: "i64", 0x7D: "f32", 0x7C: "f64"}


def lower(ctype):
    """Lowers a C type from pinvoke-table.h to a WebAssembly value type (None for void)."""
    t = ctype.strip()
    if t == "void":
        return None
    if "*" in t:
        return "i32"
    if t in ("int64_t", "uint64_t"):
        return "i64"
    if t == "float":
        return "f32"
    if t == "double":
        return "f64"
    if t in ("int32_t", "uint32_t", "int16_t", "uint16_t", "int8_t", "uint8_t", "int", "char"):
        return "i32"
    raise ValueError(f"unknown type in pinvoke-table.h: {ctype}")


def table_signatures(table, module):
    section = re.search(r"static PinvokeImport " + re.escape(module) + r"_imports \[\] = \{(.*?)\};", table, re.S)
    if not section:
        sys.exit(f"module {module} not found in the pinvoke table")

    signatures = {}
    for name in re.findall(r'\{"(\w+)",', section.group(1)):
        decl = re.search(r"^(.+?) " + name + r" \((.*?)\);", table, re.M)
        if not decl:
            sys.exit(f"{module}: no prototype for {name} in the pinvoke table")
        args = [a for a in decl.group(2).split(",") if a.strip() and a.strip() != "void"]
        signatures[name] = ([lower(a) for a in args], lower(decl.group(1)))
    return signatures


def leb(data, pos):
    result = shift = 0
    while True:
        b = data[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        shift += 7
        if not b & 0x80:
            return result, pos


def limits(data, pos):
    flags, pos = leb(data, pos)
    _, pos = leb(data, pos)
    if flags & 1:
        _, pos = leb(data, pos)
    return pos


def object_imports(path):
    """Function imports of a wasm object file: name -> (params, result)."""
    data = open(path, "rb").read()
    if data[:4] != b"\0asm":
        sys.exit(f"{path} is not a WebAssembly object")
    pos = 8
    types, imports = [], {}
    while pos < len(data):
        section, pos = data[pos], pos + 1
        size, pos = leb(data, pos)
        end = pos + size
        if section == 1:
            count, p = leb(data, pos)
            for _ in range(count):
                if data[p] != 0x60:
                    sys.exit(f"{path}: unexpected type form {data[p]:#x}")
                p += 1
                n, p = leb(data, p)
                params = [VALTYPES[data[p + i]] for i in range(n)]
                p += n
                n, p = leb(data, p)
                results = [VALTYPES[data[p + i]] for i in range(n)]
                p += n
                types.append((params, results[0] if results else None))
        elif section == 2:
            count, p = leb(data, pos)
            for _ in range(count):
                n, p = leb(data, p)
                p += n
                n, p = leb(data, p)
                field = data[p:p + n].decode()
                p += n
                kind, p = data[p], p + 1
                if kind == 0:  # function
                    index, p = leb(data, p)
                    imports[field] = types[index]
                elif kind == 1:  # table
                    p = limits(data, p + 1)
                elif kind == 2:  # memory
                    p = limits(data, p)
                elif kind == 3:  # global
                    p += 2
                else:  # tag
                    p += 1
                    _, p = leb(data, p)
        pos = end
    return imports


def compile_refs(emcc, cflags, headers, names, workdir):
    source = os.path.join(workdir, "refs.c")
    obj = os.path.join(workdir, "refs.o")
    with open(source, "w") as f:
        for h in headers:
            f.write(f"#include {h}\n")
        f.write("void *playra_pinvoke_refs[] = {\n")
        for name in names:
            f.write(f"\t(void *)&{name},\n")
        f.write("};\n")
    result = subprocess.run([emcc, "-c", "-O0", "-w", "-ferror-limit=0", *cflags, source, "-o", obj],
                            capture_output=True, text=True)
    return result, obj


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--table", required=True)
    parser.add_argument("--module", required=True)
    parser.add_argument("--emcc", required=True)
    parser.add_argument("--allowlist")
    parser.add_argument("--cflag", action="append", default=[])
    parser.add_argument("headers", nargs="+")
    args = parser.parse_args()

    module = args.module
    signatures = table_signatures(open(args.table).read(), module)

    allowed = {}
    if args.allowlist:
        for line in open(args.allowlist):
            line = line.split("#", 1)[0].strip()
            if line:
                mod, name, reason = (line.split(None, 2) + [""])[:3]
                if mod == module:
                    allowed[name] = reason

    with tempfile.TemporaryDirectory() as workdir:
        names = sorted(signatures)
        result, obj = compile_refs(args.emcc, args.cflag, args.headers, names, workdir)
        undeclared = set(re.findall(r"use of undeclared identifier '(\w+)'", result.stderr))
        if result.returncode != 0:
            if not undeclared:
                sys.exit(f"{module}: compiling the header references failed:\n{result.stderr}")
            names = [n for n in names if n not in undeclared]
            result, obj = compile_refs(args.emcc, args.cflag, args.headers, names, workdir)
            if result.returncode != 0:
                sys.exit(f"{module}: compiling the header references failed:\n{result.stderr}")
        declared = object_imports(obj)

    def fmt(sig):
        params, result = sig
        return f"({', '.join(params)}) -> {result or 'void'}"

    problems = allowed_mismatches = 0
    for name in names:
        if name not in declared:
            # static inline in the headers: there is no import to call with the wrong type.
            continue
        if signatures[name] == declared[name]:
            if name in allowed:
                print(f"{module}: {name}: allowlisted but matches now, remove it from the allowlist")
            continue
        detail = f"DllImport {fmt(signatures[name])} vs C {fmt(declared[name])}"
        if name in allowed:
            allowed_mismatches += 1
            print(f"{module}: {name}: allowed mismatch, {detail} ({allowed[name]})")
        else:
            problems += 1
            print(f"{module}: {name}: MISMATCH, {detail}")

    if undeclared:
        print(f"{module}: {len(undeclared)} imports not declared in the headers (abort stubs if called): {', '.join(sorted(undeclared))}")
    print(f"{module}: {len(signatures)} imports checked, {problems} mismatches, {allowed_mismatches} allowlisted")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
