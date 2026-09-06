#!/usr/bin/env python3
"""Self-test for the body-writing loop inside `Tools/serve-web.sh`.

    python3 Tools/serve-web-selftest.py        # exit 0 = pass

The loop it covers exists because macOS exhausts its mbuf pool when a large body is written to loopback
and fails the send with ENOBUFS *part way through*. That is not a hypothetical: it was reported against
the ~229 MB uncompressed development player. The stock `shutil`/`sendall` path cannot recover — `sendall`
reports no progress when it raises, so the body is truncated and the browser reports only a vague wasm
instantiation failure, pointing nowhere near the server.

This runs the REAL loop, extracted from the script rather than copied, so the two cannot drift. Both arms
meet an identical, deterministic injection: any difference is the code, not luck. The stock arm is the RED
control — it must fail, or this file proves nothing.
"""
import errno, io, os, pathlib, shutil, sys

SCRIPT = pathlib.Path(__file__).with_name("serve-web.sh")
CUT    = "http.server.ThreadingHTTPServer.allow_reuse_address"


def load_handler():
    """Exec the script's embedded server up to the point it would start listening, and return Handler."""
    body = SCRIPT.read_text().split("<<'PY'")[1].rsplit("PY", 1)[0]
    if CUT not in body:
        sys.exit(f"FAIL: {SCRIPT.name} no longer contains {CUT!r} — this test's cut point moved, and "
                 f"without it the exec below starts the real server and hangs. Fix the marker.")
    body = body.split(CUT)[0].replace(
        "ROOT, PORT = sys.argv[1], int(sys.argv[2])", "ROOT, PORT = '.', 0")
    assert "serve_forever" not in body, "cut left the listen call in — would hang"
    ns = {}
    exec(compile(body, str(SCRIPT), "exec"), ns)
    return ns["Handler"]


class FlakySocket:
    """Fails every 3rd write with ENOBUFS and never accepts a whole buffer.

    The partial write is the trap: a retry that resends its whole buffer duplicates the bytes the kernel
    already took, which corrupts the body just as surely as truncating it.
    """

    #: A correct loop needs a few hundred writes for this payload. A defect that fails to advance the
    #: buffer loops forever, which without this ceiling hangs the test instead of failing it — that is
    #: not hypothetical, it is what the first RED-verification of this file actually did.
    MAX_WRITES = 10_000

    def __init__(self):
        self.out, self.calls, self.hits = bytearray(), 0, 0

    def _maybe_fail(self):
        self.calls += 1
        if self.calls > self.MAX_WRITES:
            raise AssertionError(
                f"the write loop made more than {self.MAX_WRITES} calls for "
                f"{len(self.out)} bytes — it is not advancing through the buffer")
        if self.calls % 3 == 0:
            self.hits += 1
            raise OSError(errno.ENOBUFS, "No buffer space available")

    def send(self, view):                     # what the fixed loop calls
        self._maybe_fail()
        n = max(1, len(view) // 3)
        self.out += bytes(view[:n])
        return n

    def sendall(self, chunk):                 # what the stock copyfile path calls
        self._maybe_fail()
        self.out += bytes(chunk)


def main():
    handler = load_handler()

    class Detached(handler):
        def __init__(self, sock): self.connection = sock   # skip the socketserver ctor

    # Four stock copy buffers, so the control arm makes enough writes for the every-3rd injection to land.
    # Sized against shutil's real buffer: a payload smaller than one buffer is a single write, and the
    # control comes back green having never been injected at all.
    payload = os.urandom(4 * shutil.COPY_BUFSIZE)

    fixed = FlakySocket()
    try:
        Detached(fixed).copyfile(io.BytesIO(payload), None)
    except AssertionError as exc:
        print(f"FAIL: {exc}")
        return 1
    if bytes(fixed.out) != payload:
        print(f"FAIL: retry loop delivered {len(fixed.out)} of {len(payload)} bytes, not byte-exact")
        return 1
    if fixed.hits == 0:
        print("FAIL: no ENOBUFS was injected — this test proved nothing")
        return 1

    class Writer:
        def __init__(self, sock): self.sock = sock
        def write(self, chunk): self.sock.sendall(chunk); return len(chunk)

    control = FlakySocket()
    try:
        shutil.copyfileobj(io.BytesIO(payload), Writer(control))
    except OSError as exc:
        print(f"PASS: retry loop byte-exact across {fixed.hits} injected ENOBUFS; "
              f"stock path raised {exc.errno} after {len(control.out)}/{len(payload)} bytes")
        return 0
    print(f"FAIL: the stock path survived {control.hits} injections — the control did not reproduce the "
          f"bug, so passing the fixed arm says nothing")
    return 1


if __name__ == "__main__":
    sys.exit(main())
