#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Writes the browser content manifest of a mod from its own content installer definitions.

usage: content-manifest.py <engine dir> <mod id> <out.json>

Reads the mod's ModContent (the content mod named by ContentInstallerMod in mods/<mod>/mod.yaml)
and its Downloads files, the same data the desktop content installer uses: the required packages
with their test files, the quick download package, and for each download its SHA1, mirror list
URL and the files it extracts. The page installs content from this manifest, and the relay serves
only the packages listed in it (mirror list host, exact package hashes).
"""

import json
import os
import re
import sys


def parse_miniyaml(path):
    """Minimal MiniYaml reader: tab-indented "Key: Value" nodes, '#' comments. Returns a list of
    (key, value, children) tuples."""
    root = []
    stack = [(-1, root)]
    with open(path, encoding="utf-8-sig") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            stripped = line.lstrip("\t")
            if not stripped.strip() or stripped.lstrip().startswith("#"):
                continue

            if line.startswith(" "):
                sys.exit(f"{path}: space indentation is not supported: {line!r}")

            depth = len(line) - len(stripped)
            key, sep, value = stripped.partition(":")
            if not sep:
                key, value = stripped, ""

            node = (key.strip(), value.strip(), [])
            while stack[-1][0] >= depth:
                stack.pop()

            stack[-1][1].append(node)
            stack.append((depth, node[2]))

    return root


def child(nodes, key):
    for k, v, c in nodes:
        if k == key:
            return v, c

    return None, None


def resolve(path, mod_dirs):
    """Resolves a MiniYaml file reference such as racontent|installer/downloads.yaml."""
    alias, _, rel = path.partition("|")
    if alias not in mod_dirs:
        sys.exit(f"unknown package alias {alias!r} in {path!r}")

    return os.path.join(mod_dirs[alias], rel)


def support_path(p):
    p = p.strip()
    if not p.startswith("^SupportDir|"):
        sys.exit(f"content path outside the support dir: {p!r}")

    rel = p[len("^SupportDir|"):]
    if rel.startswith("/") or ".." in rel.split("/"):
        sys.exit(f"bad content path {p!r}")

    return "/support/" + rel


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)

    engine, mod, out = sys.argv[1:]
    mod_yaml = parse_miniyaml(os.path.join(engine, "mods", mod, "mod.yaml"))
    content_mod = None
    for k, v, c in mod_yaml:
        found, _ = child(c, "ContentInstallerMod")
        if found:
            content_mod = found

    if not content_mod:
        sys.exit(f"mods/{mod}/mod.yaml has no ContentInstallerMod")

    content_dir = os.path.join(engine, "mods", content_mod)
    content_yaml = parse_miniyaml(os.path.join(content_dir, "mod.yaml"))
    mod_dirs = {}
    _, filesystem = child(content_yaml, "FileSystem")
    _, packages = child(filesystem or [], "Packages")
    for k, v, _ in packages or []:
        if k.startswith("$") and v:
            mod_dirs[v] = os.path.join(engine, "mods", k[1:])

    _, mod_content = child(content_yaml, "ModContent")
    if mod_content is None:
        sys.exit(f"mods/{content_mod}/mod.yaml has no ModContent")

    content_for, _ = child(mod_content, "Mod")
    quick, _ = child(mod_content, "QuickDownload")
    required = []
    _, package_nodes = child(mod_content, "Packages")
    for k, v, c in package_nodes or []:
        req, _ = child(c, "Required")
        if (req or "").lower() != "true":
            continue

        ident, _ = child(c, "Identifier")
        tests, _ = child(c, "TestFiles")
        download, _ = child(c, "Download")
        required.append({
            "id": ident,
            "download": download,
            "testFiles": [support_path(t) for t in (tests or "").split(",") if t.strip()],
        })

    downloads = []
    _, download_files = child(mod_content, "Downloads")
    for ref, _, _ in download_files or []:
        for k, v, c in parse_miniyaml(resolve(ref, mod_dirs)):
            dtype, _ = child(c, "Type")
            sha1, _ = child(c, "SHA1")
            mirrors, _ = child(c, "MirrorList")
            _, extract = child(c, "Extract")
            if not re.fullmatch(r"[0-9a-f]{40}", sha1 or ""):
                sys.exit(f"download {k}: bad SHA1 {sha1!r}")
            if not re.fullmatch(r"https://[A-Za-z0-9.-]+/[A-Za-z0-9._/-]+", mirrors or ""):
                sys.exit(f"download {k}: bad MirrorList {mirrors!r}")

            downloads.append({
                "id": k,
                "title": v,
                "type": dtype,
                "sha1": sha1,
                "mirrorList": mirrors,
                "extract": [[support_path(target), source] for target, source, _ in extract or []],
            })

    manifest = {
        "mod": content_for or mod,
        "contentMod": content_mod,
        "quickDownload": quick,
        "required": required,
        "packages": downloads,
    }

    with open(out, "w") as f:
        json.dump(manifest, f, indent=1)
        f.write("\n")

    print(f"{out}: {len(required)} required packages, {len(downloads)} downloads, quick download {quick}")


if __name__ == "__main__":
    main()
