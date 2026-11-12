#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Hosting data for a static publish of the browser build (object storage behind a CDN, e.g. R2 and
Cloudflare): the Content-Security-Policy, and the publish manifest that lists every object to upload
with its headers.

usage:
  publish.py csp <dist> [--meta]
      The page's Content-Security-Policy for the build in <dist> (its data/build.json names the relay and
      the master route). --meta: the form for <meta http-equiv>, without the directives a meta policy
      ignores (frame-ancestors).
  publish.py manifest <dist> <out.json> [--encoding br|identity]
      Writes the publish manifest (format "playra-publish/1", see below).
  publish.py check <publish.json> <dist> <content dir>
      Checks that every object the manifest lists is there with the bytes it names, the game content
      packages included. Run it before an upload.
  publish.py first-visit <dist> [--encoding br|identity]
      Adds "firstVisit" to the build's data/build.json: what a first visit downloads, up to the main menu and
      the maps package right after it (see first_visit below). The page's start screen shows the size and
      checks the list against the browser's cache. Run it after the brotli copies are made and before the manifest.

The manifest is all an uploader needs; it never decides a header itself:

  files[]           one entry per object, keyed by its path under the base path the build is
                    published at (at the root of its own origin, as for Lanhouse, the key is the object
                    name; under a path the uploader adds the prefix, e.g. "playra/". The page finds its
                    files relative to itself, so either works):
    key               "index.html", "_framework/dotnet.native.<fingerprint>.wasm", ...
    from, source      where the bytes are: "dist" (relative to the dist directory) or "content" (the
                      directory tools/fetch-content.py wrote; game content is never part of a build)
    contentType       the Content-Type metadata
    contentEncoding   "br" when the object holds the brotli-compressed bytes (source is the .br copy),
                      else null. Object storage like R2 does not negotiate: one object, one encoding
    cacheControl      the Cache-Control metadata (immutable for content-addressed names, a day for the
                      page icons, no-cache for everything else)
    immutable         true for content-addressed names (fingerprint, hash or SHA1 in the name)
    size, sha256      of the bytes to upload (sha1 for game content, from the mod's downloads.yaml;
                      size and sha256 are null until the file exists)
    phase             upload order: 1 every immutable object, 2 the other files, 3 index.html last,
                      so a visitor never gets a page that names files not uploaded yet
  site              what the CDN must do, since object storage cannot set these itself:
    headers           response headers for every object under the base path (Content-Security-Policy
                      and the other security headers)
    rewrites          [{from, to}] paths served from another object ("<base>" -> "<base>index.html": object
                      storage has no index documents; at the root, "/" -> "/index.html")
    redirects         [{from, to, status, when}] ("<base without the slash>" -> "<base>", only when the base
                      is a path, not the root)
  neverImmutable    the keys that must never be cached as immutable (they keep their names from build to
                    build: the page, its scripts, dotnet.js, build.json, the content manifest, the web
                    app manifest, the icons)

Encodings: --encoding br (the default) uploads brotli copies of every compressible file that brotli
makes at least 5 % smaller (the .NET SDK writes them for _framework and the page's files, build-web.sh
for the rest; tools/brotli.mjs checks every copy against its file). Every browser that runs this build
accepts br over https. --encoding identity uploads the plain files and leaves compression to the CDN.
"""

import hashlib
import json
import os
import re
import sys
from urllib.parse import urlsplit

FORMAT = "playra-publish/1"

# Files whose name never changes for other bytes: fingerprinted _framework files (.NET SDK), the data
# bundles named by their hash (build-web.sh), game content packages named by their SHA1.
IMMUTABLE = re.compile(r"^(_framework/.+\.[a-z0-9]{10}\.(wasm|js|dat)|data/(engine|maps)\.[0-9a-f]{16}\.(bin|json)|content/[A-Za-z0-9_-]+\.[0-9a-f]{40}\.zip)$")

CONTENT_TYPES = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".json": "application/json",
    ".wasm": "application/wasm",
    ".dat": "application/octet-stream",
    ".bin": "application/octet-stream",
    ".zip": "application/zip",
    ".svg": "image/svg+xml",
    ".png": "image/png",
    ".ico": "image/x-icon",
    ".webmanifest": "application/manifest+json",
}

COMPRESSIBLE = {".html", ".js", ".json", ".wasm", ".dat", ".bin", ".svg"}
SIBLINGS = (".br", ".gz", ".zst")

CACHE_IMMUTABLE = "public, max-age=31536000, immutable"
CACHE_REVALIDATE = "no-cache"
# The page icons keep their names from build to build (browsers ask for /favicon.ico by that name), so they
# cannot be immutable. Nothing depends on their bytes matching the rest of the build, and a stale icon
# for a day after a change is harmless, so they get a day instead of a revalidation on every visit.
ICONS = re.compile(r"^(favicon\.(svg|ico)|favicon-[0-9]+\.png|apple-touch-icon\.png|icon(-maskable)?-[0-9]+\.png)$")
CACHE_ICON = "public, max-age=86400"

# Hosts the engine itself talks to: the server list, the resource center (maps), mod metadata and
# icons, the forum (account link).
OPENRA_HOSTS = ["https://master.openra.net", "https://resource.openra.net", "https://www.openra.net", "https://forum.openra.net"]


# The size of each game content package the page may download, by SHA1 (the mod's downloads.yaml names
# packages by hash only; fetch-content.py prints the size of what it fetched). A first-visit estimate needs
# the size of every package in the content plan: a new package fails the estimate until it is added here.
CONTENT_SIZES = {
    "44241f68e69db9511db82cf83c174737ccda300b": 13502031,  # ra-quickinstall
}


def fail(message):
    sys.exit(f"publish.py: {message}")


def load_build(dist):
    path = os.path.join(dist, "data", "build.json")
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError) as e:
        fail(f"{path}: {e}")


def url_source(value, ws):
    """A CSP source for a relay URL from build.json, or None when it is a path on the page's own site
    ('self' covers it). A WebSocket URL is matched exactly; an http(s) base URL as a path prefix."""
    if not value:
        return None

    u = urlsplit(value)
    if not u.scheme:
        return None

    if u.scheme not in (("ws", "wss") if ws else ("http", "https")) or not u.hostname or u.query or u.fragment or u.username:
        fail(f"cannot make a CSP source of {value!r}")

    path = u.path or "/"
    if not ws and not path.endswith("/"):
        path += "/"

    if not re.fullmatch(r"[A-Za-z0-9._~/%-]*", path):
        fail(f"unexpected characters in {value!r}")

    return f"{u.scheme}://{u.netloc}{path}"


def content_security_policy(build, meta=False):
    connect = ["'self'"]
    for value, ws in ((build.get("proxy"), True), (build.get("masterRelay"), False)):
        source = url_source(value, ws)
        if source and source not in connect:
            connect.append(source)

    connect += OPENRA_HOSTS
    directives = [
        "default-src 'self'",
        # wasm-unsafe-eval: the .NET runtime compiles WebAssembly at run time (jiterpreter).
        "script-src 'self' 'wasm-unsafe-eval'",
        # blob: the hidden-tab clock (main.js).
        "worker-src 'self' blob:",
        "connect-src " + " ".join(connect),
        # data: SDL's mouse cursors.
        "img-src 'self' data: blob:",
        "media-src 'self' data: blob:",
        "style-src 'self' 'unsafe-inline'",
        "font-src 'self'",
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'none'",
    ]
    # A policy in <meta http-equiv> ignores frame-ancestors (and report-uri, sandbox); browsers warn
    # about it, so it is left out there. The header form keeps it.
    if not meta:
        directives.append("frame-ancestors 'none'")

    return "; ".join(directives)


def site_headers(build):
    return {
        "Content-Security-Policy": content_security_policy(build),
        "X-Content-Type-Options": "nosniff",
        "Referrer-Policy": "no-referrer",
        "Cross-Origin-Opener-Policy": "same-origin",
        # Other sites cannot embed our files (our bandwidth, and the content packages).
        "Cross-Origin-Resource-Policy": "same-origin",
        "Strict-Transport-Security": "max-age=31536000",
    }


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def content_plan(manifest):
    """The packages the page downloads (playra-content.js plan()): the quick download if it provides
    every required test file, otherwise each required package's own download."""
    by_id = {p["id"]: p for p in manifest["packages"]}
    needed = [f for r in manifest["required"] for f in r["testFiles"]]
    quick = by_id.get(manifest.get("quickDownload"))
    if quick and set(needed) <= {target for target, _ in quick["extract"]}:
        return [quick]

    return [by_id[r["download"]] for r in manifest["required"]]


def upload_source(key, path, encoding):
    """The bytes uploaded for a file of the build: (source key, path, content encoding). With br, the
    brotli copy when it is at least 5 % smaller."""
    ext = os.path.splitext(key)[1]
    if encoding == "br" and ext in COMPRESSIBLE:
        br = path + ".br"
        if not os.path.isfile(br):
            fail(f"{key} has no brotli copy (run tools/brotli.mjs)")
        if os.path.getsize(br) <= 0.95 * os.path.getsize(path):
            return key + ".br", br, "br"

    return key, path, None


def object_entry(key, source_from, source, path, encoding):
    ext = os.path.splitext(key)[1]
    if ext not in CONTENT_TYPES:
        fail(f"no Content-Type for {key}")

    immutable = bool(IMMUTABLE.match(key))
    cache = CACHE_IMMUTABLE if immutable else CACHE_ICON if ICONS.match(key) else CACHE_REVALIDATE
    if encoding:
        # Neither the CDN nor the store may decode or re-encode it: the bytes stay as uploaded.
        cache += ", no-transform"

    return {
        "key": key,
        "from": source_from,
        "source": source,
        "contentType": CONTENT_TYPES[ext],
        "contentEncoding": encoding,
        "cacheControl": cache,
        "immutable": immutable,
        "size": os.path.getsize(path) if path else None,
        "sha256": sha256_of(path) if path else None,
        "phase": 1 if immutable else 3 if key == "index.html" else 2,
    }


def make_manifest(dist, encoding):
    build = load_build(dist)
    if build.get("content"):
        fail("this build packages game content (PLAYRA_CONTENT_DIR): it must never be published")

    files = []
    for root, dirs, names in os.walk(dist):
        dirs.sort()
        for name in sorted(names):
            path = os.path.join(root, name)
            key = os.path.relpath(path, dist).replace(os.sep, "/")
            if key.endswith(SIBLINGS):
                continue

            if key.startswith("data/content.") or key.startswith("content/"):
                fail(f"{key}: game content must not be in the build")

            source, use, enc = upload_source(key, path, encoding)
            files.append(object_entry(key, "dist", source, use, enc))

    manifest_path = build.get("contentManifest") or "data/content-packages.json"
    with open(os.path.join(dist, manifest_path)) as f:
        content = json.load(f)

    if build.get("contentUrl"):
        base = build["contentUrl"].strip("/")
        if base.startswith(".") or "//" in base or not base:
            fail(f"contentUrl {build['contentUrl']!r}: only a path below the build's directory can be listed")
        for pkg in content_plan(content):
            e = object_entry(f"{base}/{pkg['file']}", "content", pkg["file"], None, None)
            e["sha1"] = pkg["sha1"]
            files.append(e)

    files.sort(key=lambda e: (e["phase"], e["key"]))
    total = sum(e["size"] or 0 for e in files)
    return {
        "format": FORMAT,
        "build": {k: build.get(k) for k in ("version", "commit", "dev", "aot", "configuration", "target")},
        "encoding": encoding,
        "files": files,
        "bytes": total,
        "neverImmutable": [e["key"] for e in files if not e["immutable"]],
        "site": {
            "headers": site_headers(build),
            "rewrites": [{"from": "<base>", "to": "<base>index.html"}],
            "redirects": [{"from": "<base without the trailing slash>", "to": "<base>", "status": 301, "when": "the base is not /"}],
        },
    }


def framework_boot_config(dist):
    """The resources of the .NET loader's boot config, embedded in _framework/dotnet.js."""
    path = os.path.join(dist, "_framework", "dotnet.js")
    with open(path, encoding="utf-8") as f:
        text = f.read()

    start, end = text.find("/*json-start*/"), text.find("/*json-end*/")
    if start < 0 or end < start:
        fail(f"{path}: no embedded boot config (/*json-start*/ ... /*json-end*/)")

    return json.loads(text[start + len("/*json-start*/"):end])["resources"]


def framework_first_visit(resources):
    """The _framework files a first visit loads, and the ICU data shards. Loaded at startup: the runtime and
    native modules, the WebAssembly binary, every assembly (none are lazy-loaded) and one ICU shard, which
    the loader picks by the browser's language. The count takes icudt_no_CJK, the one for Portuguese and
    most languages (EFIGS, for English, French, Italian, German and Spanish, is about 0.08 MB smaller, CJK
    about 0.03 MB larger)."""
    names = []
    for kind in ("jsModuleNative", "jsModuleRuntime", "wasmNative", "coreAssembly", "assembly"):
        names += [r["name"] for r in resources.get(kind) or []]

    if resources.get("lazyAssembly"):
        fail("_framework/dotnet.js: lazy-loaded assemblies are not counted; update first_visit")

    icu = [f"_framework/{r['name']}" for r in resources.get("icu") or []]
    shard = [n for n in icu if n.startswith("_framework/icudt_no_CJK.")]
    return ["_framework/dotnet.js"] + [f"_framework/{n}" for n in names] + (shard or icu), icu


def first_visit(dist, encoding):
    """What a first visit transfers, in the bytes the publish uploads (the brotli copies with --encoding br):
    the page and its modules, the icon the page links first, the start screen's logo, build.json, the content manifest, the .NET
    runtime and assemblies, the engine data and the game content (a local test bundle, or the packages of
    the content plan, downloaded from our site or through the relay), and the maps package, which the page
    downloads right after the main menu is up. Not counted: HTTP headers. "cached" lists the content-addressed files
    among them, which a returning visit finds in the browser's HTTP cache (the game content is kept in
    IndexedDB instead, and found there). "cachedOneOf" lists the ICU shards: a returning visit has the one its
    language uses."""
    build = load_build(dist)
    keys = ["index.html", "favicon.svg"]
    # The start screen's logo (build-web.sh PLAYRA_SPLASH), shown with the page.
    keys += [n for n in ("splash-logo.svg", "splash-logo.png") if os.path.isfile(os.path.join(dist, n))]
    keys += sorted(n for n in os.listdir(dist) if n.endswith(".js") and os.path.isfile(os.path.join(dist, n)))
    keys += ["data/build.json", build.get("contentManifest") or "data/content-packages.json"]
    engine = build.get("engineData")
    if not engine:
        fail("build.json names no engineData")
    keys += [f"{engine}.json", f"{engine}.bin"]
    maps = build.get("mapData")
    if maps:
        keys += [f"{maps}.json", f"{maps}.bin"]
    if build.get("content"):
        keys += ["data/content.json", "data/content.bin"]
    framework, icu = framework_first_visit(framework_boot_config(dist))
    keys += framework

    total = 0
    for key in keys:
        path = os.path.join(dist, key)
        if not os.path.isfile(path):
            fail(f"first visit: {key} is not in the build")
        total += os.path.getsize(upload_source(key, path, encoding)[1])

    if not build.get("content"):
        with open(os.path.join(dist, build.get("contentManifest") or "data/content-packages.json")) as f:
            for pkg in content_plan(json.load(f)):
                if pkg["sha1"] not in CONTENT_SIZES:
                    fail(f"first visit: the size of the content package {pkg['file']} is unknown; add it to CONTENT_SIZES in tools/publish.py")
                total += CONTENT_SIZES[pkg["sha1"]]

    cached = [k for k in keys if k not in icu and (IMMUTABLE.match(k) or (build.get("content") and k.startswith("data/content.")))]
    return {"bytes": total, "cached": cached, "cachedOneOf": icu}


def check(manifest_path, dist, content_dir):
    with open(manifest_path) as f:
        manifest = json.load(f)

    if manifest.get("format") != FORMAT:
        fail(f"{manifest_path}: not a {FORMAT} manifest")

    problems = 0
    for e in manifest["files"]:
        root = dist if e["from"] == "dist" else content_dir
        path = os.path.join(root, e["source"])
        if not os.path.isfile(path):
            print(f"missing: {e['key']} ({e['from']}:{e['source']})")
            problems += 1
            continue

        if "sha1" in e:
            h = hashlib.sha1()
            with open(path, "rb") as f:
                for chunk in iter(lambda: f.read(1 << 20), b""):
                    h.update(chunk)
            if h.hexdigest() != e["sha1"]:
                print(f"wrong bytes: {e['key']} (SHA1 {h.hexdigest()}, the mod says {e['sha1']})")
                problems += 1
        elif sha256_of(path) != e["sha256"] or os.path.getsize(path) != e["size"]:
            print(f"wrong bytes: {e['key']} ({e['from']}:{e['source']})")
            problems += 1

    print(f"{len(manifest['files'])} objects, {problems} problems")
    return problems == 0


def main():
    args = sys.argv[1:]
    if len(args) >= 2 and args[0] == "csp":
        print(content_security_policy(load_build(args[1]), meta="--meta" in args[2:]))
    elif len(args) >= 3 and args[0] == "manifest":
        encoding = "br"
        if "--encoding" in args:
            encoding = args[args.index("--encoding") + 1]
        if encoding not in ("br", "identity"):
            fail("--encoding must be br or identity")
        manifest = make_manifest(args[1], None if encoding == "identity" else "br")
        manifest["encoding"] = encoding
        with open(args[2], "w") as f:
            json.dump(manifest, f, indent=1)
            f.write("\n")
        n = len(manifest["files"])
        print(f"{args[2]}: {n} objects, {manifest['bytes'] / 1e6:.1f} MB to upload ({encoding}), "
              f"{len(manifest['neverImmutable'])} never immutable")
    elif len(args) >= 2 and args[0] == "first-visit":
        encoding = "br"
        if "--encoding" in args:
            encoding = args[args.index("--encoding") + 1]
        if encoding not in ("br", "identity"):
            fail("--encoding must be br or identity")
        path = os.path.join(args[1], "data", "build.json")
        build = load_build(args[1])
        build["firstVisit"] = first_visit(args[1], None if encoding == "identity" else "br")
        with open(path, "w") as f:
            json.dump(build, f, separators=(",", ":"))
            f.write("\n")
        print(f"{path}: first visit {build['firstVisit']['bytes'] / 1e6:.1f} MB with the maps ({encoding}), "
              f"{len(build['firstVisit']['cached'])} files found in the HTTP cache on a returning visit")
    elif len(args) == 4 and args[0] == "check":
        sys.exit(0 if check(*args[1:]) else 1)
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
