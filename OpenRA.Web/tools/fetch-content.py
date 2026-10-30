#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Fetches the game content packages the browser page installs, once, from the official OpenRA
mirrors, verified, for publishing on our own site next to the build.

usage: fetch-content.py <engine dir> <mod> <out dir> [--manifest <dist>/data/content-packages.json]
                        [--from <file.zip>] [--all]

The packages, their SHA1 and their mirror lists come from the mod's own content installer
definitions (mods/<mod>-content/installer/downloads.yaml, read by content-manifest.py), the same data
the desktop installer uses. For each package the page downloads (the quick download, or each
required package's own; --all: every download) it writes <out dir>/<file>, the content-addressed
name the page asks for (<mod>-<package>.<sha1>.zip). Nothing is fetched for a file that is already
there with the right SHA1: run it once, then reuse the directory.

A download picks the mirrors in random order from the package's mirror list
(https://www.openra.net/packages/...), accepts only https, never follows a redirect off the
official mirror hosts, stops at 64 MiB, and keeps the file only if its SHA1 is the mod's.
--from <file.zip> takes a copy you already have instead (checked the same way).
--manifest also checks that a build's manifest names the same packages and hashes.

The build never packages these files (build-web.sh): they are freeware Red Alert data that we host
for our players instead of sending every first visit to the community mirrors. Upload them with the
build's publish manifest (tools/publish.py), which lists them under "content/".
"""

import hashlib
import importlib.util
import json
import os
import random
import shutil
import sys
import tempfile
import urllib.error
import urllib.request
from urllib.parse import urlsplit

# The official mirror list host and the mirrors it lists today (the relay's defaults, source/proxy).
MIRROR_LIST_HOSTS = {"www.openra.net"}
MIRROR_HOSTS = {"openra.baxxster.no", "openra.ppmsite.com", "republic.community", "openra.0x47.net"}
MAX_BYTES = 64 << 20
TIMEOUT = 60
USER_AGENT = "webra-fetch-content/1 (one-time copy for self-hosting)"


def fail(message):
    sys.exit(f"fetch-content.py: {message}")


def load_manifest_tool():
    sys.dont_write_bytecode = True
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "content-manifest.py")
    spec = importlib.util.spec_from_file_location("content_manifest", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def plan(manifest, everything):
    by_id = {p["id"]: p for p in manifest["packages"]}
    if everything:
        return manifest["packages"]

    needed = [f for r in manifest["required"] for f in r["testFiles"]]
    quick = by_id.get(manifest.get("quickDownload"))
    if quick and set(needed) <= {target for target, _ in quick["extract"]}:
        return [quick]

    return [by_id[r["download"]] for r in manifest["required"]]


def sha1_of(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


OPENER = urllib.request.build_opener(NoRedirect)


def get(url, hosts, limit):
    u = urlsplit(url)
    if u.scheme != "https" or u.hostname not in hosts or u.username or u.port not in (None, 443):
        raise ValueError(f"{url}: not an https URL on {', '.join(sorted(hosts))}")

    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with OPENER.open(req, timeout=TIMEOUT) as r:
        if r.status != 200:
            raise ValueError(f"{url}: HTTP {r.status}")
        data = r.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f"{url}: larger than {limit} bytes")
    return data


def download(pkg, out_path):
    mirrors = [line.strip() for line in get(pkg["mirrorList"], MIRROR_LIST_HOSTS, 64 << 10).decode().splitlines() if line.strip()]
    random.shuffle(mirrors)
    errors = []
    for mirror in mirrors:
        try:
            data = get(mirror, MIRROR_HOSTS, MAX_BYTES)
        except (OSError, ValueError, urllib.error.URLError) as e:
            errors.append(str(e))
            continue

        digest = hashlib.sha1(data).hexdigest()
        if digest != pkg["sha1"]:
            errors.append(f"{mirror}: SHA1 {digest}, the mod says {pkg['sha1']}")
            continue

        write(out_path, data)
        return mirror, len(data)

    fail(f"{pkg['id']}: no mirror gave the package:\n  " + "\n  ".join(errors or ["empty mirror list"]))


def write(out_path, data):
    fd, tmp = tempfile.mkstemp(dir=os.path.dirname(out_path), prefix=".partial-")
    with os.fdopen(fd, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.chmod(tmp, 0o644)
    os.replace(tmp, out_path)


def main():
    args = sys.argv[1:]
    options = {}
    for flag in ("--manifest", "--from"):
        if flag in args:
            i = args.index(flag)
            if i + 1 >= len(args):
                fail(f"{flag} needs a value")
            options[flag] = args[i + 1]
            del args[i:i + 2]
    everything = "--all" in args
    args = [a for a in args if a != "--all"]
    if len(args) != 3:
        sys.exit(__doc__)

    engine, mod, out = args
    manifest = load_manifest_tool().build_manifest(engine, mod)
    if "--manifest" in options:
        with open(options["--manifest"]) as f:
            built = json.load(f)
        theirs = {p["id"]: (p["sha1"], p.get("file")) for p in built["packages"]}
        ours = {p["id"]: (p["sha1"], p["file"]) for p in manifest["packages"]}
        if theirs != ours:
            fail(f"{options['--manifest']} names other packages than {engine}/mods: {theirs} != {ours}")

    os.makedirs(out, exist_ok=True)
    packages = plan(manifest, everything)
    if "--from" in options and len(packages) != 1:
        fail("--from needs exactly one package to fetch")

    for pkg in packages:
        path = os.path.join(out, pkg["file"])
        if os.path.isfile(path) and sha1_of(path) == pkg["sha1"]:
            print(f"{pkg['file']}: already there, SHA1 {pkg['sha1']} (nothing fetched)")
            continue

        if "--from" in options:
            digest = sha1_of(options["--from"])
            if digest != pkg["sha1"]:
                fail(f"{options['--from']}: SHA1 {digest}, the mod says {pkg['sha1']}")
            shutil.copyfile(options["--from"], path + ".partial")
            os.chmod(path + ".partial", 0o644)
            os.replace(path + ".partial", path)
            print(f"{pkg['file']}: copied from {options['--from']}, SHA1 {digest}")
            continue

        mirror, size = download(pkg, path)
        print(f"{pkg['file']}: {size} bytes from {mirror}, SHA1 {pkg['sha1']} (as downloads.yaml)")


if __name__ == "__main__":
    main()
