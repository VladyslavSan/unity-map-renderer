#!/usr/bin/env bash
# Fills the cache folder of the Online tests: Assets/Fixtures/online~/ (git-ignored, ignored by Unity).
# Not part of Tools/run-tests.sh. After it ran once, the Online tests need no network.
#
#   Tools/fetch-test-tiles.sh
#
# Tiles: the pinned OpenFreeMap build first, then the sibling checkout ../unity-map-renderer-test-tiles, then a server of that
# repository on $UMR_TEST_TILES_URL (default http://localhost:8000). Goldens: the sibling checkout, then that server.
# Exits 1 when a file has no source.
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
CACHE="$ROOT/Assets/Fixtures/online~"
SIBLING="$ROOT/../unity-map-renderer-test-tiles"
LOCAL="${UMR_TEST_TILES_URL:-http://localhost:8000}"
PINNED="https://tiles.openfreemap.org/planet/20260927_080001_pt"

TILES="6/34/21 6/38/19 9/274/168 6/32/20 8/135/80 8/145/99 9/279/187 8/220/128 8/132/72 8/212/120 9/282/150 14/4825/6156"
GOLDENS="line-graphwrite-golden-z6-WebMercator.json line-graphwrite-golden-z6-Spherical.json line-graphwrite-golden-z9-WebMercator.json line-graphwrite-golden-z9-Spherical.json"

remote_down=0
failed=0

# fetch_file REL SOURCE...  A SOURCE is a URL, or SIBLING for the sibling checkout. The first that answers wins.
fetch_file() {
  local rel="$1"; shift
  local dest="$CACHE/$rel"
  [ -s "$dest" ] && return 0
  mkdir -p "$(dirname "$dest")"
  for source in "$@"; do
    if [ "$source" = SIBLING ]; then
      if [ -s "$SIBLING/$rel" ]; then cp "$SIBLING/$rel" "$dest"; return 0; fi
      continue
    fi
    # Only the first connection failure to the pinned build is paid for: it is skipped afterwards. An HTTP error (curl 22) is not a failure to connect.
    case "$source" in "$PINNED"*) [ "$remote_down" = 1 ] && continue ;; esac
    rc=0
    curl -fsS --max-time 20 "$source" -o "$dest.part" 2>/dev/null || rc=$?
    if [ "$rc" = 0 ]; then mv "$dest.part" "$dest"; return 0; fi
    rm -f "$dest.part"
    case "$source" in "$PINNED"*) [ "$rc" != 22 ] && remote_down=1 ;; esac
  done
  return 1
}

for t in $TILES; do
  rel="tiles/$t.pbf"
  if ! fetch_file "$rel" "$PINNED/$t.pbf" SIBLING "$LOCAL/$rel"; then echo "no source for $rel" >&2; failed=1; fi
done
for g in $GOLDENS; do
  rel="goldens/$g"
  if ! fetch_file "$rel" SIBLING "$LOCAL/$rel"; then echo "no source for $rel" >&2; failed=1; fi
done

[ "$failed" = 0 ] && echo "online test data is in $CACHE" || exit 1
