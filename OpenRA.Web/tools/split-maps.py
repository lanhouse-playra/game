#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Moves the bundled maps the main menu does not need out of a staged engine tree.

usage: split-maps.py <engine stage dir> <maps stage dir> <mod>...

The page loads the engine data before the menu and the maps package after it. The menu needs only
the shellmaps (map.yaml "Visibility" includes Shellmap): they stay in mods/<mod>/maps of the engine
tree. Every other map (an .oramap file or a map directory) moves to the same relative path under
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


def is_shellmap(path):
    match = VISIBILITY.search(map_yaml(path))
    return match is not None and "Shellmap" in [v.strip() for v in match.group(1).split(",")]


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
        for name in sorted(os.listdir(folder)):
            path = os.path.join(folder, name)
            if not (os.path.isdir(path) or name.endswith(".oramap")):
                continue

            if is_shellmap(path):
                kept += 1
                continue

            os.makedirs(target, exist_ok=True)
            shutil.move(path, os.path.join(target, name))
            moved += 1

    if kept == 0 and moved > 0:
        sys.exit("split-maps.py: no shellmap found; the main menu needs one in the engine data")

    print(f"split-maps.py: {kept} shellmap(s) stay in the engine data, {moved} maps go to the maps package")


if __name__ == "__main__":
    main()
