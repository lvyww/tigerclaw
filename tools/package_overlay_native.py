#!/usr/bin/env python3
"""Package existing MSVC native Overlay builds without touching a runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=("ARM64", "x64"), required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    source = root / "next/TigerClaw.Overlay.Native"
    build = root / "next/_run/OverlayNative" / args.arch / "Release"
    names = ["TigerClaw.Overlay.exe", "TigerClaw.Overlay.Native.Preview.exe", "THIRD-PARTY-NOTICES.txt",
             "sounds/KeyNormal.wav", "sounds/KeySpace.wav", "sounds/KeyFunc.wav"]
    payload = {name: (build / name).read_bytes() for name in names}
    for name in names[:2]:
        data = payload[name]
        if data[:2] != b"MZ":
            raise ValueError(f"Invalid executable: {name}")
        offset = struct.unpack_from("<I", data, 0x3C)[0]
        expected = 0xAA64 if args.arch == "ARM64" else 0x8664
        if data[offset:offset + 4] != b"PE\0\0" or struct.unpack_from("<H", data, offset + 4)[0] != expected:
            raise ValueError(f"Wrong architecture: {name}")
    if payload[names[0]] != payload[names[1]]:
        raise ValueError("Preview copy does not match replacement executable")
    built_at = (build / names[0]).stat().st_mtime
    for path in list(source.glob("*.cpp")) + list(source.glob("*.h")):
        if path.stat().st_mtime > built_at + 2:
            raise ValueError(f"Rebuild before packaging: {path.name} is newer than executable")
    payload["README.txt"] = (source / "PACKAGE-README.txt").read_bytes()
    hashes = {name: hashlib.sha256(data).hexdigest() for name, data in payload.items()}
    identity = hashlib.sha256(json.dumps(hashes, sort_keys=True).encode()).hexdigest()[:12]
    payload["SHA256SUMS.json"] = json.dumps(dict(architecture=args.arch, files=hashes), indent=2).encode() + b"\n"
    output = root / "next/_run/OverlayNativePackages" / f"TigerClaw-Overlay-Native-{args.arch}-{identity}.zip"
    output.parent.mkdir(parents=True, exist_ok=True)
    if not output.exists():
        with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED) as archive:
            for name, data in payload.items():
                archive.writestr(name, data)
    with zipfile.ZipFile(output) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(payload):
            raise ValueError("Package CRC or file list mismatch")
        for name, data in payload.items():
            if archive.read(name) != data:
                raise ValueError(f"Package content mismatch: {name}")
    print(f"Verified {args.arch} package: {output}")
    print(f"Package SHA256: {hashlib.sha256(output.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
