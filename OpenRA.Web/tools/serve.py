#!/usr/bin/env python3
# Copyright (c) The OpenRA Developers and Contributors
# This file is part of OpenRA, which is free software. It is made
# available to you under the terms of the GNU General Public License
# as published by the Free Software Foundation, either version 3 of
# the License, or (at your option) any later version. For more
# information, see COPYING.

"""Serves OpenRA.Web/dist on 127.0.0.1 for local testing.

usage: serve.py <dist dir> [port]

Sends COOP/COEP so the page is cross-origin isolated (needed later for threads and precise
timers), and the .wasm MIME type. Binds to loopback only.
"""

import functools
import http.server
import sys


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map,
                      ".wasm": "application/wasm", ".js": "text/javascript", ".mjs": "text/javascript",
                      ".json": "application/json", ".bin": "application/octet-stream"}

    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cache-Control", "no-cache")
        super().end_headers()

    def log_message(self, format, *args):
        pass


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)

    port = int(sys.argv[2]) if len(sys.argv) > 2 else 8090
    handler = functools.partial(Handler, directory=sys.argv[1])
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    print(f"Serving {sys.argv[1]} on http://127.0.0.1:{port}/", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
