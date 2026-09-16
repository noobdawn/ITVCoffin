#!/usr/bin/env python3
"""extract_bundles.py — discover the game's hot-update bundle and dump its TextAssets.

The hot-update bundle is the UnityFS bundle under the client's CacheFiles tree that contains
a TextAsset named ``Assembly-CSharp.dll``. Its location (hash directory) differs between game
versions, so this script scans for it instead of hard-coding a path.

Usage:
    python extract_bundles.py --game "<game dir>" [--out extracted] [--bundle <__data>]

``--game`` may point at any of:
    * the game root (the folder that contains ``IntoTheVoid_Data``)
    * the ``IntoTheVoid_Data`` folder
    * the ``.../intothevoid/intothevoid`` folder
    * the ``CacheFiles`` folder itself
If ``--game`` is omitted, the ``ITV_GAME_DIR`` environment variable is used.

Requires: UnityPy (see requirements.txt).
"""

import argparse
import os
import re
import sys

try:
    import UnityPy
except ImportError:
    UnityPy = None  # reported in main() so this module stays importable

MARKER = b"Assembly-CSharp.dll"
DLL_TEXTASSET = "Assembly-CSharp.dll"
UNITYFS_MAGIC = b"UnityFS"

# Bundle-name / hash patterns inside the launcher's PackageManifest_*.bytes.
HOTUPDATE_NAME_RX = re.compile(rb"([A-Za-z0-9_./]*hotupdate[A-Za-z0-9_./]*\.bundle)", re.IGNORECASE)
HEX32_RX = re.compile(rb"[0-9a-f]{32}")


def manifest_files_dir(cache_dir):
    """The launcher's ManifestFiles folder normally sits next to CacheFiles."""
    d = os.path.join(os.path.dirname(os.path.abspath(cache_dir)), "ManifestFiles")
    return d if os.path.isdir(d) else None


def manifest_hotupdate_hashes(cache_dir):
    """Bundle hashes whose bundle name contains 'hotupdate' (fast hint from the manifest)."""
    mf = manifest_files_dir(cache_dir)
    if not mf:
        return []
    hashes = []
    for fn in sorted(os.listdir(mf)):
        if not fn.endswith(".bytes"):
            continue
        try:
            with open(os.path.join(mf, fn), "rb") as f:
                blob = f.read()
        except OSError:
            continue
        for m in HOTUPDATE_NAME_RX.finditer(blob):
            h = HEX32_RX.search(blob[m.end():m.end() + 160])
            if h:
                s = h.group(0).decode("ascii")
                if s not in hashes:
                    hashes.append(s)
    return hashes


def bundle_path_for_hash(cache_dir, h):
    return os.path.join(cache_dir, h[:2], h, "__data")


def is_unityfs(path):
    try:
        with open(path, "rb") as f:
            return f.read(8).startswith(UNITYFS_MAGIC)
    except OSError:
        return False


def resolve_cache_dir(game_arg):
    """Best-effort resolution of a CacheFiles directory from a user-supplied path."""
    if not game_arg:
        return None
    game_arg = os.path.abspath(game_arg)

    candidates = [
        game_arg,
        os.path.join(game_arg, "CacheFiles"),
        os.path.join(game_arg, "IntoTheVoid_Data", "intothevoid", "intothevoid", "CacheFiles"),
        os.path.join(game_arg, "intothevoid", "intothevoid", "CacheFiles"),
    ]
    for c in candidates:
        if os.path.isdir(c) and os.path.basename(c.rstrip("\\/")) == "CacheFiles":
            return c

    # Bounded walk: look for a directory literally named CacheFiles.
    for root, dirs, _files in os.walk(game_arg):
        if os.path.basename(root) == "CacheFiles":
            return root
        # Do not descend into unrelated deep trees.
        if root.count(os.sep) - game_arg.count(os.sep) > 6:
            dirs[:] = []
    return None


def iter_bundle_data_files(cache_dir):
    """Yield every ``<xx>/<hash>/__data`` path under a CacheFiles directory."""
    for sub in sorted(os.listdir(cache_dir)):
        subdir = os.path.join(cache_dir, sub)
        if not os.path.isdir(subdir):
            continue
        for h in sorted(os.listdir(subdir)):
            hdir = os.path.join(subdir, h)
            data = os.path.join(hdir, "__data")
            if os.path.isfile(data):
                yield data


def _bundle_has_dll_textasset(path):
    """Authoritative check: load the bundle and look for the TextAsset by name."""
    if UnityPy is None:
        raise RuntimeError("UnityPy is not installed")
    try:
        env = UnityPy.load(path)
    except Exception:
        return False
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        try:
            data = obj.read()
        except Exception:
            continue
        if getattr(data, "m_Name", None) == DLL_TEXTASSET:
            return True
    return False


def find_hotupdate_bundle(cache_dir, verbose=True):
    """Find the bundle containing the Assembly-CSharp.dll TextAsset.

    Fast path: resolve the bundle hash from the launcher's PackageManifest (bundle names are
    stable across versions, hash directories are not). Fallback: scan CacheFiles from largest
    to smallest and verify each candidate with UnityPy (the required, version-proof method).
    """
    # Fast path: manifest hint, verified with UnityPy.
    for h in manifest_hotupdate_hashes(cache_dir):
        p = bundle_path_for_hash(cache_dir, h)
        if os.path.isfile(p) and is_unityfs(p):
            if verbose:
                print("  manifest hint: %s -> %s" % (h, p))
            if _bundle_has_dll_textasset(p):
                return p

    # Fallback: scan the cache.
    cands = []
    for data in iter_bundle_data_files(cache_dir):
        try:
            cands.append((os.path.getsize(data), data))
        except OSError:
            continue
    cands.sort(reverse=True)

    # Largest UnityFS bundles that contain the marker string, verified with UnityPy.
    for size, path in cands:
        if not is_unityfs(path):
            continue
        try:
            with open(path, "rb") as f:
                blob = f.read()
        except OSError:
            continue
        if MARKER not in blob:
            continue
        if verbose:
            print("  candidate: %s (%d bytes)" % (path, size))
        if _bundle_has_dll_textasset(path):
            return path

    # Full authoritative scan (slower, handles compressed bundles).
    if verbose:
        print("  no raw marker match; falling back to full UnityPy scan ...")
    for size, path in cands:
        if not is_unityfs(path):
            continue
        if _bundle_has_dll_textasset(path):
            return path
    return None


def dump_textassets(bundle_path, outdir):
    if UnityPy is None:
        raise RuntimeError("UnityPy is not installed")
    os.makedirs(outdir, exist_ok=True)
    env = UnityPy.load(bundle_path)
    n = 0
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        name = getattr(data, "m_Name", None) or ("obj_%d" % obj.path_id)
        val = data.m_Script
        raw = val.encode("utf-8", "surrogateescape") if isinstance(val, str) else bytes(val)
        safe = name.replace("/", "_").replace("\\", "_")
        with open(os.path.join(outdir, safe), "wb") as f:
            f.write(raw)
        n += 1
        print("  %-60s len=%-10d head=%s" % (name, len(raw), raw[:4].hex()))
    return n


def main():
    ap = argparse.ArgumentParser(description="Discover + extract the hot-update bundle TextAssets.")
    ap.add_argument("--game", "-g", default=os.environ.get("ITV_GAME_DIR", ""),
                    help="game directory (root / IntoTheVoid_Data / CacheFiles)")
    ap.add_argument("--out", "-o", default="extracted", help="output directory (default: extracted)")
    ap.add_argument("--bundle", default="", help="explicit path to a bundle __data file (skip discovery)")
    ap.add_argument("--no-copy", action="store_true",
                    help="do not copy the source bundle to <out>/hotupdate_original.bundle")
    args = ap.parse_args()

    if UnityPy is None:
        print("ERROR: UnityPy is not installed. Run: pip install -r requirements.txt")
        return 2

    bundle = args.bundle
    if not bundle:
        cache_dir = resolve_cache_dir(args.game)
        if not cache_dir:
            print("ERROR: could not locate CacheFiles. Pass --game \"<game dir>\".")
            print("       Tip: the game dir is the folder containing 'IntoTheVoid_Data'.")
            return 2
        print("CacheFiles:", cache_dir)
        print("Scanning for the hot-update bundle (TextAsset 'Assembly-CSharp.dll') ...")
        bundle = find_hotupdate_bundle(cache_dir)
        if not bundle:
            print("ERROR: no bundle containing TextAsset 'Assembly-CSharp.dll' was found.")
            return 1

    print("hot-update bundle:", bundle)

    if not args.no_copy:
        os.makedirs(args.out, exist_ok=True)
        dst = os.path.join(args.out, "hotupdate_original.bundle")
        with open(bundle, "rb") as fi, open(dst, "wb") as fo:
            fo.write(fi.read())
        print("copied original bundle ->", dst)

    print("extracting TextAssets ->", args.out)
    n = dump_textassets(bundle, args.out)
    print("-> extracted %d TextAssets" % n)
    print("DONE")
    return 0


if __name__ == "__main__":
    sys.exit(main())
