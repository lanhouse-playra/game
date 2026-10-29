#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Diffs two outputs of the numeric conformance corpus (desktop and browser).

usage: conformance-diff.py [--strict] <desktop.txt> <browser.txt>

Lines are "section key = value". Sections starting with "info." describe the runtime and may differ.
Sections ending in ".libm" hold transcendental functions (sin, atan2, log10, ...), whose last bit
depends on the C library: glibc, the Windows CRT, Apple's libm and musl differ from each other for
some arguments, so desktop clients already disagree there. Their differences are listed and counted
but only fail with --strict. Every other line must be identical; the script lists the differences
per section and exits 1 if there are any.
"""

import sys


def load(path):
    entries = {}
    for line in open(path, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line:
            continue
        head, _, value = line.partition(" = ")
        section, _, key = head.partition(" ")
        entries[(section, key)] = value
    return entries


def main():
    argv = sys.argv[1:]
    strict = "--strict" in argv
    argv = [x for x in argv if x != "--strict"]
    if len(argv) != 2:
        sys.exit(__doc__)
    a, b = load(argv[0]), load(argv[1])
    gated = sorted(k for k in set(a) | set(b) if not k[0].startswith("info."))
    sections = {}
    for k in gated:
        sections.setdefault(k[0], [0, []])
        sections[k[0]][0] += 1
        if a.get(k) != b.get(k):
            sections[k[0]][1].append(k[1])

    differences = libm = 0
    for section, (count, keys) in sorted(sections.items()):
        print(f"{section}: {count} checks, {len(keys)} differ")
        for key in keys:
            if section.endswith(".libm"):
                libm += 1
            else:
                differences += 1
            va, vb = a.get((section, key), "<missing>"), b.get((section, key), "<missing>")
            if section.endswith("libm"):
                # One line per function with every sweep input: show only the differing positions.
                xs, ys = va.split(" "), vb.split(" ")
                diff = [f"#{i}: {x} vs {y}" for i, (x, y) in enumerate(zip(xs, ys)) if x != y]
                print(f"  {key}: {len(diff)} of {len(xs)} inputs differ: {'; '.join(diff[:6])}{' ...' if len(diff) > 6 else ''}")
            else:
                print(f"  {key}\n    desktop: {va}\n    browser: {vb}")

    print(f"total: {len(gated)} checks, {differences} differences, {libm} libm (transcendental) differences")
    sys.exit(1 if differences or (strict and libm) else 0)


if __name__ == "__main__":
    main()
