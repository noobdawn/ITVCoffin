#!/usr/bin/env python3
"""parse_capture.py — parse a proxy capture log and dump server->client payloads.

Reads the newest ``tcp_*.log`` from the capture directory (or an explicit log file), prints a
table of every framed contract, and writes each non-empty server->client payload to
``<captureDir>/parsed/`` as a ``.bin`` file.

The log line format produced by the proxy is:
    <HH:mm:ss.fff> [C->S|S->C] Data type=<Type> id=<id> route='<route>' dataLen=<n> data=<HEX>

Usage:
    python parse_capture.py [--captures captures] [--log <tcp_xxx.log>] [--out captures/parsed]
"""

import argparse
import glob
import os
import re
import sys

LINE_RX = re.compile(
    r"^(\d\d:\d\d:\d\d\.\d+) \[(C->S|S->C)\] Data type=(\w+) id=(\d+) "
    r"route='([^']*)' dataLen=(\d+) data=(.*)$"
)


def main():
    ap = argparse.ArgumentParser(description="Parse a proxy capture log.")
    ap.add_argument("--captures", default="captures",
                    help="capture directory to search for tcp_*.log (default: captures)")
    ap.add_argument("--log", default="",
                    help="explicit log file (default: newest tcp_*.log in --captures)")
    ap.add_argument("--out", default="",
                    help="output directory for dumped payloads (default: <captures>/parsed)")
    args = ap.parse_args()

    logfile = args.log
    if not logfile:
        logs = sorted(glob.glob(os.path.join(args.captures, "tcp_*.log")))
        if not logs:
            print("ERROR: no tcp_*.log found in %s" % os.path.abspath(args.captures))
            return 1
        logfile = logs[-1]  # newest by name (timestamped)
    if not os.path.isfile(logfile):
        print("ERROR: log not found:", logfile)
        return 1

    out = args.out or os.path.join(os.path.dirname(logfile) or args.captures, "parsed")
    os.makedirs(out, exist_ok=True)

    print("log:", logfile)
    print("out:", out)
    with open(logfile, encoding="utf-8", errors="replace") as f:
        lines = f.read().splitlines()

    entries = []
    for ln in lines:
        m = LINE_RX.match(ln)
        if not m:
            continue
        t, direction, ctype, mid, route, dlen, data = m.groups()
        entries.append({
            "t": t, "dir": direction, "type": ctype, "id": int(mid),
            "route": route, "len": int(dlen),
            "hex": data, "line": ln,
        })

    print("parsed entries:", len(entries))
    print("=" * 100)
    print(f"{'#':>4} {'time':>12} {'dir':>6} {'type':<9} {'id':>10} {'len':>8}  route")
    print("-" * 100)
    for i, e in enumerate(entries):
        print(f"{i:>4} {e['t']:>12} {e['dir']:>6} {e['type']:<9} {e['id']:>10} {e['len']:>8}  {e['route']}")

    # dump server->client payloads for messages with data
    n = 0
    for e in entries:
        if e["dir"] == "S->C" and e["len"] > 0:
            name = "%03d_%s_id%d.bin" % (
                n,
                e["route"].replace(".", "_").replace("/", "_") or "noRoute",
                e["id"],
            )
            with open(os.path.join(out, name), "wb") as f:
                f.write(bytes.fromhex(e["hex"]))
            n += 1
    print("\ndumped %d server->client payloads to %s" % (n, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
