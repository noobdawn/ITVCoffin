#!/usr/bin/env python3
"""patch_client.py — one-shot client patcher (locate -> extract -> patch -> repack -> apply).

Runs the whole client-modification pipeline in order:

    [1/5] locate the game's hot-update bundle (CacheFiles scan)
    [2/5] copy the original bundle + extract Assembly-CSharp.dll
    [3/5] patch the DLL with ITVCoffin.Patcher (endpoints -> 127.0.0.1, RSA bypass)
    [4/5] repack the patched DLL back into the bundle
    [5/5] install the bundle into the game cache (backup + rebuild __info)

Usage:
    python patch_client.py --game "<game dir>"
    python patch_client.py --game "<game dir>" --dry-run     # everything except writing the cache

The game directory is the folder that contains ``IntoTheVoid_Data`` (or pass the CacheFiles
folder directly). If ``--game`` is omitted, the ``ITV_GAME_DIR`` environment variable is used.

Requires: Python 3.9+, UnityPy (requirements.txt), .NET 9 SDK (to run the patcher).
"""

import argparse
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)

import extract_bundles as eb  # noqa: E402  (local module)

DLL_TEXTASSET = "Assembly-CSharp.dll"


def step(n, msg):
    print("\n[%d/5] %s" % (n, msg))


def extract_dll(bundle_path, out_dll):
    if eb.UnityPy is None:
        raise RuntimeError("UnityPy is not installed (pip install -r requirements.txt)")
    env = eb.UnityPy.load(bundle_path)
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        if data.m_Name != DLL_TEXTASSET:
            continue
        val = data.m_Script
        raw = val.encode("utf-8", "surrogateescape") if isinstance(val, str) else bytes(val)
        with open(out_dll, "wb") as f:
            f.write(raw)
        return len(raw)
    raise RuntimeError("TextAsset '%s' not found in bundle" % DLL_TEXTASSET)


def run_patcher(patcher_arg, in_dll, out_dll):
    """Run ITVCoffin.Patcher. Prefers a built DLL, falls back to `dotnet run --project`."""
    cmds = []
    if patcher_arg:
        if patcher_arg.endswith(".dll"):
            cmds.append(["dotnet", patcher_arg, in_dll, out_dll])
        else:
            cmds.append([patcher_arg, in_dll, out_dll])
    else:
        for cfg in ("Release", "Debug"):
            dll = os.path.join(ROOT, "patcher", "bin", cfg, "net9.0", "ITVCoffin.Patcher.dll")
            if os.path.isfile(dll):
                cmds.append(["dotnet", dll, in_dll, out_dll])
        proj = os.path.join(ROOT, "patcher", "ITVCoffin.Patcher.csproj")
        if os.path.isfile(proj):
            cmds.append(["dotnet", "run", "--project", proj, "-c", "Release", "--", in_dll, out_dll])

    if not cmds:
        raise RuntimeError("no patcher found (build patcher/ or pass --patcher)")

    last_err = None
    for cmd in cmds:
        pretty = " ".join('"%s"' % c if " " in c else c for c in cmd)
        print("  $", pretty)
        try:
            rc = subprocess.run(cmd, cwd=ROOT).returncode
        except FileNotFoundError as e:
            last_err = e
            continue
        if rc == 0:
            return True
        last_err = "exit code %d" % rc
    raise RuntimeError("patcher failed: %s" % last_err)


def main():
    ap = argparse.ArgumentParser(description="One-shot ITVCoffin client patcher.")
    ap.add_argument("--game", "-g", default=os.environ.get("ITV_GAME_DIR", ""),
                    help="game dir (contains IntoTheVoid_Data) or CacheFiles dir")
    ap.add_argument("--work", "-w", default="work", help="working dir for intermediate files (default: work)")
    ap.add_argument("--patcher", default="", help="path to a built ITVCoffin.Patcher.dll/exe (optional)")
    ap.add_argument("--dry-run", action="store_true",
                    help="run the whole pipeline but do NOT write to the game cache")
    args = ap.parse_args()

    if eb.UnityPy is None:
        print("ERROR: UnityPy is not installed. Run: pip install -r requirements.txt")
        return 2

    os.makedirs(args.work, exist_ok=True)

    step(1, "locating the game's hot-update bundle")
    cache = eb.resolve_cache_dir(args.game)
    if not cache:
        print("ERROR: could not locate CacheFiles. Pass --game \"<game dir>\".")
        print("       The game dir is the folder that contains 'IntoTheVoid_Data'.")
        return 2
    print("  CacheFiles:", cache)
    bundle = eb.find_hotupdate_bundle(cache)
    if not bundle:
        print("ERROR: no bundle containing TextAsset '%s' was found." % DLL_TEXTASSET)
        return 1
    print("  bundle:", bundle)

    step(2, "copying original bundle + extracting %s" % DLL_TEXTASSET)
    orig = os.path.join(args.work, "hotupdate_original.bundle")
    shutil.copyfile(bundle, orig)
    dll_in = os.path.join(args.work, "Assembly-CSharp.dll")
    n = extract_dll(orig, dll_in)
    print("  extracted %d bytes -> %s" % (n, dll_in))

    step(3, "patching %s" % DLL_TEXTASSET)
    dll_out = os.path.join(args.work, "Assembly-CSharp.patched.dll")
    try:
        run_patcher(args.patcher, dll_in, dll_out)
    except RuntimeError as e:
        print("ERROR:", e)
        return 1
    if not os.path.isfile(dll_out):
        print("ERROR: patcher did not produce", dll_out)
        return 1
    print("  patched ->", dll_out)

    step(4, "repacking the bundle")
    patched = os.path.join(args.work, "hotupdate_patched.bundle")
    rc = subprocess.run([sys.executable, os.path.join(HERE, "repack_hotupdate.py"),
                         orig, dll_out, patched]).returncode
    if rc != 0:
        print("ERROR: repack failed")
        return 1
    print("  repacked ->", patched)

    step(5, "installing into the game cache")
    bundle_dir = os.path.dirname(bundle)
    if args.dry_run:
        print("  --dry-run: skipped. Would install %s into %s" % (patched, bundle_dir))
        print("\nDRY RUN OK (game cache untouched)")
        return 0
    rc = subprocess.run([sys.executable, os.path.join(HERE, "apply_bundle.py"),
                         patched, "--bundle-dir", bundle_dir]).returncode
    if rc != 0:
        print("ERROR: apply failed")
        return 1

    print("\nDONE. The client is patched. Start the proxy, then launch the game.")
    print("      (Restore the original at any time from __data.bak / __info.bak.)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
