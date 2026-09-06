#!/usr/bin/env bash
# Serve a web player build locally. Self-locating; run from anywhere inside the repo.
#
#   Tools/serve-web.sh [--dev] [dir] [port]   (default: Builds/Web/UnityMapRenderer, 8080)
#
# --dev serves the DEVELOPMENT player (Builds/Web-Development/), the one `Tools/build.sh web --dev`
# writes. The flag spelling is deliberately the same in both scripts: build.sh --dev and serve-web.sh
# without it silently serve DIFFERENT players, and the only symptom is a release player reporting
# devBuild=False long after you built a development one.
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
# build_artifact is the single mirror of where BuildScript writes; hardcoding Builds/Web here is the
# exact bug this script shipped with.
. "$ROOT/Tools/lib.sh"
DEV=0 DEVFLAG=""
if [ "${1:-}" = --dev ]; then DEV=1 DEVFLAG=" --dev"; shift; fi
# A lone all-digits argument is a PORT, not a directory. Without this `serve-web.sh --dev 8099` reads
# 8099 as the build dir and dies with "no build at 8099".
case "${1:-}" in ''|*[!0-9]*) ;; *) set -- "" "$1" ;; esac
DIR="${1:-$ROOT/$(build_artifact web "$DEV")}"
PORT="${2:-8080}"

[ -d "$DIR" ]            || { echo "no build at $DIR — run: Tools/build.sh web${DEVFLAG}" >&2; exit 2; }
[ -f "$DIR/index.html" ] || { echo "$DIR has no index.html — is it a web build?" >&2; exit 2; }

case "$DIR" in
  *-Development/*) echo "serving the DEVELOPMENT player (Debug.isDebugBuild=true, counters compiled in)" >&2 ;;
  *)               echo "serving the RELEASE player — pass --dev for the development one" >&2 ;;
esac
echo "serving $DIR on http://localhost:$PORT"
exec python3 - "$DIR" "$PORT" <<'PY'
import errno, http.server, sys, time
ROOT, PORT = sys.argv[1], int(sys.argv[2])

class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=ROOT, **k)

    def copyfile(self, source, outputfile):
        # macOS runs its mbuf pool dry when a large body is blasted at loopback and fails the send with
        # ENOBUFS (errno 55) PART WAY THROUGH the body. sendall — which is what the stock copyfile path
        # uses — reports no progress on failure, so retrying its buffer would duplicate bytes and hand the
        # browser a corrupt wasm; the page then fails at instantiation with nothing pointing back here.
        # send() reports what it wrote, so write the body straight to the socket and back off on ENOBUFS,
        # which is transient. The DEVELOPMENT player is what surfaces this: ~229 MB uncompressed, against
        # ~8 MB Brotli for release, is enough to exhaust the pool on a local reload.
        sock = self.connection
        while True:
            chunk = source.read(1 << 18)
            if not chunk:
                return
            view = memoryview(chunk)
            while view:
                try:
                    view = view[sock.send(view):]
                except OSError as e:
                    if e.errno != errno.ENOBUFS:
                        raise
                    # Short on purpose. The kernel drains its mbufs in microseconds, but it also
                    # accepts small partial writes under pressure, so this can run once per few KB —
                    # at 50ms that arithmetic turns a 229 MB body into minutes.
                    time.sleep(0.002)

    def handle(self):
        # A browser that navigates away, reloads, or cancels mid-transfer drops the socket, and the
        # copy raises. Nothing is wrong with the build or the server, so log one line instead of a
        # traceback. The DEVELOPMENT player makes this routine: its wasm is ~229 MB uncompressed
        # (the release one is ~8 MB Brotli), so a reload during load reliably aborts a transfer.
        try:
            super().handle()
        except (BrokenPipeError, ConnectionResetError):
            self.log_message("client disconnected mid-transfer")

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

# THREADING is not a nicety here. The loader fetches the wasm, the framework and the data
# concurrently, and a single-threaded server serialises them behind the largest — which, for the
# development player, is a ~229 MB body. One stalled or aborted transfer then blocks every other
# request, including the ones the page needs to report why it is stuck.
http.server.ThreadingHTTPServer.allow_reuse_address = True
with http.server.ThreadingHTTPServer(("127.0.0.1", PORT), Handler) as httpd:
    httpd.serve_forever()
PY
