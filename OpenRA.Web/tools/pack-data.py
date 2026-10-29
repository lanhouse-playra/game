#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Packs a directory tree into <out>.bin plus an <out>.json index for the browser loader.

usage: pack-data.py <source dir> <virtual target dir> <out prefix>

Every regular file under <source dir> is appended to <out>.bin. The index maps each file to its
path under <virtual target dir> in the emscripten filesystem, with its offset and size.
"""

import json
import os
import sys


def main():
    if len(sys.argv) != 4:
        sys.exit(__doc__)

    source, target, out = sys.argv[1], sys.argv[2].rstrip("/"), sys.argv[3]
    files = []
    offset = 0
    with open(out + ".bin", "wb") as blob:
        for root, dirs, names in os.walk(source):
            dirs.sort()
            for name in dirs + names:
                # A symlink could pull files from outside the tree into the published data.
                if os.path.islink(os.path.join(root, name)):
                    sys.exit(f"refusing to pack symlink {os.path.join(root, name)}")

            for name in sorted(names):
                path = os.path.join(root, name)
                if not os.path.isfile(path):
                    continue

                with open(path, "rb") as f:
                    data = f.read()

                blob.write(data)
                rel = os.path.relpath(path, source).replace(os.sep, "/")
                files.append({"path": target + "/" + rel, "offset": offset, "size": len(data)})
                offset += len(data)

    with open(out + ".json", "w") as index:
        json.dump({"size": offset, "files": files}, index, separators=(",", ":"))

    print(f"{out}.bin: {len(files)} files, {offset} bytes")


if __name__ == "__main__":
    main()
