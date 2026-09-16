#!/usr/bin/env python3
"""repack_hotupdate.py — replace the Assembly-CSharp.dll TextAsset inside a bundle.

Loads a source UnityFS bundle, replaces the bytes of the TextAsset named
``Assembly-CSharp.dll`` with a supplied DLL, saves a new bundle and reload-verifies it.

Usage:
    python repack_hotupdate.py [<source_bundle> <dll_path> <out_bundle>]

All three positional arguments are optional; the defaults are relative paths inside the
current directory so the tool stays portable:

    source_bundle : work/hotupdate_original.bundle
    dll_path      : work/Assembly-CSharp.patched.dll
    out_bundle    : work/hotupdate_patched.bundle

Requires: UnityPy (see requirements.txt).
"""

import os
import sys

try:
    import UnityPy
except ImportError:
    print("ERROR: UnityPy is not installed. Run: pip install -r requirements.txt")
    sys.exit(2)

DLL_TEXTASSET = "Assembly-CSharp.dll"

DEFAULT_SRC = os.path.join("work", "hotupdate_original.bundle")
DEFAULT_DLL = os.path.join("work", "Assembly-CSharp.patched.dll")
DEFAULT_OUT = os.path.join("work", "hotupdate_patched.bundle")

SRC = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SRC
DLL = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_DLL
OUT = sys.argv[3] if len(sys.argv) > 3 else DEFAULT_OUT

for label, path in (("source bundle", SRC), ("dll", DLL)):
    if not os.path.isfile(path):
        print("ERROR: %s not found: %s" % (label, path))
        sys.exit(2)

print("source bundle:", SRC)
print("dll:", DLL)
print("out bundle:", OUT)

env = UnityPy.load(SRC)
new_bytes = open(DLL, "rb").read()
print("patched DLL size:", len(new_bytes))

found = False
for obj in env.objects:
    if obj.type.name != "TextAsset":
        continue
    data = obj.read()
    if data.m_Name == DLL_TEXTASSET:
        # m_Script is typed as a string in the typetree; use surrogateescape so
        # arbitrary binary (the DLL bytes) round-trips exactly.
        data.m_Script = new_bytes.decode("utf-8", "surrogateescape")
        data.save()
        found = True
        break

if not found:
    print("ERROR: TextAsset '%s' not found" % DLL_TEXTASSET)
    sys.exit(1)

bundle_bytes = env.file.save()
print("new bundle size:", len(bundle_bytes))
out_dir = os.path.dirname(os.path.abspath(OUT))
if out_dir:
    os.makedirs(out_dir, exist_ok=True)
open(OUT, "wb").write(bundle_bytes)

# verify: reload the output
env2 = UnityPy.load(OUT)
ok = False
for obj in env2.objects:
    if obj.type.name == "TextAsset":
        d = obj.read()
        if d.m_Name == DLL_TEXTASSET:
            v = d.m_Script
            vb = v.encode("utf-8", "surrogateescape") if isinstance(v, str) else bytes(v)
            print("reload: %s bytes len %d %s" % (DLL_TEXTASSET, len(vb), "match" if vb == new_bytes else "MISMATCH"))
            ok = vb == new_bytes
print("VERIFY:", "OK" if ok else "FAIL")
sys.exit(0 if ok else 1)
