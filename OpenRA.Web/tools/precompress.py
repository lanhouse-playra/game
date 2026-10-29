#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Writes .gz (and, if the zstd tool is installed, .zst) copies of the files in a directory.

usage: precompress.py <dir>

The web server sends the smallest encoding the browser accepts (Caddy: file_server precompressed).
zstd level 19 keeps the window at 8 MiB, the most browsers accept for Content-Encoding: zstd.
Files named content.* (local test content, never published) are skipped.
"""

import gzip
import os
import shutil
import subprocess
import sys


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)

    directory = sys.argv[1]
    zstd = shutil.which("zstd")
    for name in sorted(os.listdir(directory)):
        path = os.path.join(directory, name)
        if not os.path.isfile(path) or name.endswith((".gz", ".zst", ".br")) or name.startswith("content."):
            continue

        with open(path, "rb") as f:
            data = f.read()

        with open(path + ".gz", "wb") as f:
            f.write(gzip.compress(data, 9, mtime=0))

        sizes = f"gzip {os.path.getsize(path + '.gz')}"
        if zstd:
            subprocess.run([zstd, "-q", "-19", "-f", path, "-o", path + ".zst"], check=True)
            sizes += f", zstd {os.path.getsize(path + '.zst')}"

        print(f"{name}: {len(data)} bytes, {sizes}")


if __name__ == "__main__":
    main()
