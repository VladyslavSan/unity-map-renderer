#!/usr/bin/env bash
# Serve a web player build locally. Self-locating; run from anywhere inside the repo.
#
#   Tools/serve-web.sh [dir] [port]      (default: Builds/Web/UnityMapRenderer, 8080)
#
# Why this exists instead of `python3 -m http.server`: Unity compresses the player with Brotli, and the
# loader has no JS fallback decoder (webGLDecompressionFallback: 0), so the SERVER must declare
# `Content-Encoding: br` on the .br files. A plain static server hands the browser compressed bytes with
# no encoding header and the player fails to load. Safari additionally only advertises `br` in
# Accept-Encoding over HTTPS, so on plain http it never asks for it — declaring the header regardless is
# what makes it work here.
#
# It also sends COOP/COEP/CORP, and those are LOAD-BEARING — do not strip them as dead weight.
# BuildScript.RunWeb turns web threads ON, so the player's wasm imports `env.memory` with shared=YES. That
# memory is SharedArrayBuffer-backed and its constructor throws unless the page is cross-origin-isolated,
# so a server that omits these headers does not serve a slower player — it serves one that never starts.
#
# Exit codes: 0 = served (blocks), 2 = setup error (no build found).
set -uo pipefail

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repo" >&2; exit 2; }
DIR="${1:-$ROOT/Builds/Web/UnityMapRenderer}"
PORT="${2:-8080}"

[ -d "$DIR" ]            || { echo "no build at $DIR — run: Tools/build.sh web" >&2; exit 2; }
[ -f "$DIR/index.html" ] || { echo "$DIR has no index.html — is it a web build?" >&2; exit 2; }

echo "serving $DIR on http://localhost:$PORT"
exec python3 - "$DIR" "$PORT" <<'PY'
import http.server, socketserver, sys
ROOT, PORT = sys.argv[1], int(sys.argv[2])

class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=ROOT, **k)

    def end_headers(self):
        self.send_header('Cross-Origin-Opener-Policy', 'same-origin')
        self.send_header('Cross-Origin-Embedder-Policy', 'require-corp')
        self.send_header('Cross-Origin-Resource-Policy', 'cross-origin')
        self.send_header('Cache-Control', 'no-store')
        for ext, enc in (('.br', 'br'), ('.gz', 'gzip')):
            if self.path.endswith(ext):
                self.send_header('Content-Encoding', enc)
        super().end_headers()

    def guess_type(self, path):
        # The type of the DECOMPRESSED payload, keyed off the name under the .br/.gz suffix. This has to
        # happen here rather than in end_headers: the base handler already sent a Content-Type by then, and
        # adding a second one leaves the browser choosing between them — it may pick application/octet-stream
        # and refuse to stream-compile the wasm.
        for ext in ('.br', '.gz'):
            if path.endswith(ext):
                base = path[:-len(ext)]
                return ('application/wasm'       if base.endswith('.wasm') else
                        'application/javascript' if base.endswith('.js')   else
                        'application/octet-stream')
        return super().guess_type(path)

socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(("127.0.0.1", PORT), Handler) as httpd:
    httpd.serve_forever()
PY
