#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Compares the C prototypes that the .NET wasm build generates from DllImports (pinvoke-table.h)
with the real C headers, and reports functions whose return kind (void or not) or argument count
differ. On native platforms such a mismatch is harmless; in WebAssembly the call traps with
"function signature mismatch".

usage: check-pinvoke-signatures.py <pinvoke-table.h> <module> <header>...
"""

import re
import sys


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)

    table = open(sys.argv[1]).read()
    module = sys.argv[2]
    headers = "".join(open(h, errors="replace").read() for h in sys.argv[3:])
    headers = re.sub(r"/\*.*?\*/", "", headers, flags=re.S)
    headers = re.sub(r"\s+", " ", headers)

    section = re.search(r"static PinvokeImport " + re.escape(module) + r"_imports \[\] = \{(.*?)\};", table, re.S)
    if not section:
        sys.exit(f"module {module} not found")

    # Index every prototype in the headers: "<return type> [(]name[)] (<args>)" per statement.
    prototypes = {}
    for statement in headers.split(";"):
        m = re.search(r"([\w\s\*]+?)\(?\s*(\w+)\s*\)?\s*\(([^()]*)\)\s*$", statement)
        if m:
            prototypes.setdefault(m.group(2), (m.group(1), m.group(3)))

    def argc(args):
        args = args.strip()
        return 0 if args in ("", "void") else args.count(",") + 1

    names = re.findall(r'\{"(\w+)",', section.group(1))
    problems = 0
    for name in names:
        decl = re.search(r"^(.+?) " + name + r" \((.*?)\);", table, re.M)
        if not decl or name not in prototypes:
            continue

        ret, args = prototypes[name]
        dret_void = decl.group(1).strip() == "void"
        macros = {"extern", "DECLSPEC", "SDLCALL", "LUA_API", "LUALIB_API", "AL_API", "AL_APIENTRY", "ALC_API", "ALC_APIENTRY", "FT_EXPORT"}
        tokens = [t for t in ret.split() if t not in macros]
        rret_void = tokens == ["void"]
        if dret_void != rret_void or argc(decl.group(2)) != argc(args):
            problems += 1
            print(f"{module}: {name}: generated '{decl.group(1).strip()} ({decl.group(2)})' vs header '{ret.strip()} ({args.strip()})'")

    print(f"{module}: {len(names)} imports checked, {problems} mismatches")


if __name__ == "__main__":
    main()
