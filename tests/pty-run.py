#!/usr/bin/env python3
"""Run a command on a fresh pseudo-terminal, as if typed in a terminal window.

  pty-run.py [--send-on REGEX BYTES]... [--hangup-on REGEX] [--timeout SECS] -- cmd...

--send-on     once the output matches REGEX, write BYTES (Python escapes, e.g. '\\x03' for Ctrl-C);
              repeatable, applied in order
--hangup-on   once the output matches REGEX, close the terminal (SIGHUP to its foreground group)
Prints the output (CRs removed), then "EXIT <code>" and, after a hangup,
"AFTER-HANGUP <seconds>" (how long the command took to exit after it).
Exits 1 on timeout.
"""
import argparse, os, pty, re, select, sys, time

ap = argparse.ArgumentParser()
ap.add_argument("--send-on", nargs=2, action="append", default=[], metavar=("REGEX", "BYTES"))
ap.add_argument("--hangup-on")
ap.add_argument("--timeout", type=float, default=120)
ap.add_argument("cmd", nargs=argparse.REMAINDER)
a = ap.parse_args()
cmd = a.cmd[1:] if a.cmd[:1] == ["--"] else a.cmd

pid, fd = pty.fork()
if pid == 0:
    os.execvp(cmd[0], cmd)

sends = [(re.compile(r), b.encode().decode("unicode_escape").encode("latin-1")) for r, b in a.send_on]
out = b""
hung_at = None
deadline = time.time() + a.timeout

# Read until the terminal closes (child gone) or we hang it up.
while time.time() < deadline:
    r, _, _ = select.select([fd], [], [], 0.1)
    if r:
        try:
            data = os.read(fd, 4096)
        except OSError:
            data = b""
        if not data:
            break
        out += data
    text = out.decode(errors="replace")
    while sends and sends[0][0].search(text):
        os.write(fd, sends.pop(0)[1])
    if a.hangup_on and re.search(a.hangup_on, text):
        hung_at = time.time()
        break
os.close(fd)

status = None
while time.time() < deadline:
    done, st = os.waitpid(pid, os.WNOHANG)
    if done:
        status = st
        break
    time.sleep(0.05)

print(out.decode(errors="replace").replace("\r", ""))
if status is None:
    os.kill(pid, 9)
    print("TIMEOUT")
    sys.exit(1)
print("EXIT", os.waitstatus_to_exitcode(status))
if hung_at is not None:
    print("AFTER-HANGUP %.1f" % (time.time() - hung_at))
