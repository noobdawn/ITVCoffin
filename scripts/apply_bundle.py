#!/usr/bin/env python3
"""apply_bundle.py — install a repacked hot-update bundle into the game's CacheFiles.

Writes the patched bundle over the cache's ``__data``, backs the original ``__data`` /
``__info`` up to ``.bak`` (once, non-destructive), and rebuilds ``__info`` with the correct
CRC32 + size so the launcher accepts the bundle.

Usage:
    python apply_bundle.py <patched_bundle> <game_dir | cache_dir | bundle_dir>
    python apply_bundle.py <patched_bundle> --bundle-dir "<.../xx/<hash>>"

The target may be:
    * the bundle directory itself (the folder containing ``__data`` and ``__info``)
    * a ``CacheFiles`` directory (the hot-update bundle is located automatically)
    * the game root / ``IntoTheVoid_Data`` / ``.../intothevoid/intothevoid``

The ``__info`` format is intentionally unchanged:
    b"\\x08\\x00" + crc32-as-8-hex-ascii + int64-little-endian-size
"""

import argparse
import os
import re
import shutil
import struct
import sys
import zlib

try:
    import UnityPy  # optional: only used to verify/scan compressed bundles
except ImportError:
    UnityPy = None

MARKER = b"Assembly-CSharp.dll"
UNITYFS_MAGIC = b"UnityFS"

HOTUPDATE_NAME_RX = re.compile(rb"([A-Za-z0-9_./]*hotupdate[A-Za-z0-9_./]*\.bundle)", re.IGNORECASE)
HEX32_RX = re.compile(rb"[0-9a-f]{32}")


def is_unityfs(path):
    try:
        with open(path, "rb") as f:
            return f.read(8).startswith(UNITYFS_MAGIC)
    except OSError:
        return False


def manifest_files_dir(cache_dir):
    d = os.path.join(os.path.dirname(os.path.abspath(cache_dir)), "ManifestFiles")
    return d if os.path.isdir(d) else None


def manifest_hotupdate_hashes(cache_dir):
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


def resolve_cache_dir(game_arg):
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
    for root, dirs, _files in os.walk(game_arg):
        if os.path.basename(root) == "CacheFiles":
            return root
        if root.count(os.sep) - game_arg.count(os.sep) > 6:
            dirs[:] = []
    return None


def iter_bundle_data_files(cache_dir):
    for sub in sorted(os.listdir(cache_dir)):
        subdir = os.path.join(cache_dir, sub)
        if not os.path.isdir(subdir):
            continue
        for h in sorted(os.listdir(subdir)):
            data = os.path.join(subdir, h, "__data")
            if os.path.isfile(data):
                yield data


def _has_dll_textasset(path):
    if UnityPy is None:
        return False
    try:
        env = UnityPy.load(path)
    except Exception:
        return False
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        try:
            if getattr(obj.read(), "m_Name", None) == "Assembly-CSharp.dll":
                return True
        except Exception:
            continue
    return False


def find_hotupdate_bundle_dir(cache_dir):
    """Locate the bundle directory whose __data holds the Assembly-CSharp.dll TextAsset.

    Fast path: resolve the bundle hash from the launcher's PackageManifest (the bundle NAME is
    stable across versions; the hash directory is not). Fallbacks: raw marker scan (works on an
    already-patched bundle) and, when UnityPy is installed, a full authoritative scan.
    """
    # Fast path: manifest hint.
    for h in manifest_hotupdate_hashes(cache_dir):
        d = os.path.join(cache_dir, h[:2], h)
        data = os.path.join(d, "__data")
        if os.path.isfile(data) and is_unityfs(data):
            return d

    # Fallback: marker byte-scan over bundles, largest first.
    cands = []
    for data in iter_bundle_data_files(cache_dir):
        try:
            cands.append((os.path.getsize(data), data))
        except OSError:
            continue
    cands.sort(reverse=True)
    for _size, data in cands:
        if not is_unityfs(data):
            continue
        try:
            with open(data, "rb") as f:
                blob = f.read()
        except OSError:
            continue
        if MARKER in blob:
            return os.path.dirname(data)

    # Last resort: authoritative UnityPy scan (compressed bundles).
    for _size, data in cands:
        if not is_unityfs(data):
            continue
        if _has_dll_textasset(data):
            return os.path.dirname(data)
    return None


def resolve_bundle_dir(target):
    if os.path.isdir(target) and os.path.isfile(os.path.join(target, "__data")):
        return os.path.abspath(target)
    cache = resolve_cache_dir(target)
    if not cache:
        return None
    return find_hotupdate_bundle_dir(cache)


def main():
    ap = argparse.ArgumentParser(description="Install a repacked hot-update bundle.")
    ap.add_argument("patched_bundle", help="path to the repacked bundle")
    ap.add_argument("target", nargs="?", default="",
                    help="game dir / CacheFiles / bundle dir (or use --bundle-dir)")
    ap.add_argument("--bundle-dir", default="",
                    help="explicit bundle directory (the folder containing __data/__info)")
    args = ap.parse_args()

    new_path = args.patched_bundle
    if not os.path.isfile(new_path):
        print("ERROR: patched bundle not found:", new_path)
        return 2

    if args.bundle_dir:
        bundle_dir = os.path.abspath(args.bundle_dir)
        if not os.path.isfile(os.path.join(bundle_dir, "__data")):
            print("ERROR: no __data in --bundle-dir:", bundle_dir)
            return 2
    else:
        if not args.target:
            print("ERROR: pass a game dir / CacheFiles / bundle dir, or --bundle-dir.")
            return 2
        bundle_dir = resolve_bundle_dir(args.target)
        if not bundle_dir:
            print("ERROR: could not locate the hot-update bundle under:", args.target)
            return 1

    data_path = os.path.join(bundle_dir, "__data")
    info_path = os.path.join(bundle_dir, "__info")
    print("bundle dir :", bundle_dir)

    data = open(new_path, "rb").read()
    print("new bundle size:", len(data))

    # Backup originals once (never clobber an existing backup).
    if not os.path.exists(data_path + ".bak"):
        shutil.copy(data_path, data_path + ".bak")
        if os.path.isfile(info_path):
            shutil.copy(info_path, info_path + ".bak")
        print("backed up originals -> __data.bak / __info.bak")
    else:
        print("backup already present (__data.bak kept as the pristine original)")

    # Write the new bundle.
    open(data_path, "wb").write(data)

    # Rebuild __info: 0x08 0x00 | crc32 LE-as-hex-ascii (8 bytes) | filesize int64 LE
    crc = zlib.crc32(data) & 0xFFFFFFFF
    crc_ascii = struct.pack("<I", crc).hex().encode("ascii")   # e.g. b"f5dfc8eb"
    info = b"\x08\x00" + crc_ascii + struct.pack("<q", len(data))
    open(info_path, "wb").write(info)

    print("crc value: 0x%08x  crc ascii: %s" % (crc, crc_ascii.decode()))
    print("filesize:", len(data))
    print("__info bytes:", info.hex())
    print("APPLIED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
