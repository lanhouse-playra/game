#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Moves the bundled maps the main menu does not need out of a staged engine tree.

usage: split-maps.py <engine stage dir> <maps stage dir> <mod>...

The page loads the engine data before the menu and the maps package after it. Two kinds of map stay
in mods/<mod>/maps of the engine tree:
  - the shellmaps (map.yaml "Visibility" includes Shellmap), the menu's background;
  - the smallest map shown in the lobby (Visibility includes Lobby; about 6 KB for ra). The engine
    assumes a mod always has one: the map chooser throws when neither its system nor its user tab
    has a map to select, and the main menu disables skirmish.
Every other map (an .oramap file or a map directory) moves to the same relative path under
<maps stage dir>, so both packages unpack into the same folder of the browser's file system and the
engine sees byte-identical maps once both are there.
"""

import os
import re
import shutil
import sys
import zipfile

VISIBILITY = re.compile(r"^Visibility:[ \t]*(.*?)[ \t]*$", re.MULTILINE)


def map_yaml(path):
    if os.path.isdir(path):
        with open(os.path.join(path, "map.yaml"), "rb") as f:
            return f.read().decode("utf-8-sig")

    with zipfile.ZipFile(path) as z:
        # OpenRA reads package entries case-sensitively; map.yaml is always lower case.
        return z.read("map.yaml").decode("utf-8-sig")


def visibility(path):
    # MapPreview's default when map.yaml has no Visibility.
    match = VISIBILITY.search(map_yaml(path))
    return [v.strip() for v in match.group(1).split(",")] if match else ["Lobby"]


def size(path):
    if not os.path.isdir(path):
        return os.path.getsize(path)

    return sum(os.path.getsize(os.path.join(root, name)) for root, _, names in os.walk(path) for name in names)


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)

    engine, maps, mods = sys.argv[1], sys.argv[2], sys.argv[3:]
    kept = moved = 0
    for mod in mods:
        folder = os.path.join(engine, "mods", mod, "maps")
        if not os.path.isdir(folder):
            continue

        target = os.path.join(maps, "mods", mod, "maps")
        entries = [n for n in sorted(os.listdir(folder)) if os.path.isdir(os.path.join(folder, n)) or n.endswith(".oramap")]
        shown = {n: visibility(os.path.join(folder, n)) for n in entries}
        lobby = sorted((size(os.path.join(folder, n)), n) for n in entries if "Lobby" in shown[n])
        keep = {n for n in entries if "Shellmap" in shown[n]}
        if not keep and entries:
            sys.exit(f"split-maps.py: no shellmap in mods/{mod}/maps; the main menu needs one in the engine data")

        if lobby:
            keep.add(lobby[0][1])

        for name in entries:
            if name in keep:
                print(f"split-maps.py: mods/{mod}/maps/{name} stays in the engine data")
                kept += 1
                continue

            os.makedirs(target, exist_ok=True)
            shutil.move(os.path.join(folder, name), os.path.join(target, name))
            moved += 1

    print(f"split-maps.py: {kept} maps stay in the engine data, {moved} go to the maps package")


if __name__ == "__main__":
    main()
