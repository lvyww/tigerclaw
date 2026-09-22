#!/usr/bin/env python3
"""Create a fresh, isolated full-pinyin developer package. Never installs/deploys."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "next/_run/FullPinyin"
ARCHIVE = Path("C:/Archive/tigerclaw_sentence_ml") if os.name == "nt" else Path("/mnt/c/Archive/tigerclaw_sentence_ml")
WANXIANG = ARCHIVE / "runtime/full-pinyin-wanxiang-20260922"
FROZEN = {
    "wanxiang-pinyin.txt": "f5a5215792bf2046f394621b0f37cda79015e9d8281165b11f50c1eb6c5d6b5f",
    "wanxiang-tokens.json": "4b15b39d7d4435d1e4e81673c7b621459d9df8d323fae2e4f7d8631f9e834a98",
    "joint5-q8.klm": "125c9231049fe4aadb35c90d8b4aeb81d73d7ee165f5d9c3762781a110c8dabc",
    "joint_3gram.klm": "8a9aec29184dc12ae2f4740500102ca9e48a9a77e78ffeea0be25111ca27f2fc",
    "pinyin.txt": "8b8a0ecfcae5b7b85f5a98c78615163c5d9cbdf28561aa2ee88fc44be508ad2b",
    "tokens.json": "d62b87c8798b563ca98957445b5d56962cc2bc42d668d898c94373adccb3f616",
}

def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def machine(path):
    with path.open("rb") as stream:
        if stream.read(2) != b"MZ": raise ValueError(f"Not PE: {path}")
        stream.seek(0x3c); offset = struct.unpack("<I", stream.read(4))[0]
        stream.seek(offset)
        if stream.read(4) != b"PE\0\0": raise ValueError(f"Not PE: {path}")
        return struct.unpack("<H", stream.read(2))[0]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", choices=["ARM64", "x64"], required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--rerank-model", type=Path, help="Optional frozen full five-gram Q8; trigram Top50 reranking")
    parser.add_argument("--dictionary", choices=["wanxiang", "original"], default="wanxiang")
    parser.add_argument("--tokens", type=Path)
    parser.add_argument("--table", type=Path)
    parser.add_argument("--name", required=True, help="New directory name under next/_run/FullPinyin/packages")
    args = parser.parse_args()
    if Path(args.name).name != args.name or args.name in (".", "..") or "\\" in args.name:
        raise ValueError("Package name must be a single directory component")
    target = OUT / "packages" / args.name
    if target.exists(): raise FileExistsError(f"Refusing to overwrite {target}")
    is_wanxiang = args.dictionary == "wanxiang"
    table_name = "wanxiang-pinyin.txt" if is_wanxiang else "pinyin.txt"
    tokens_name = "wanxiang-tokens.json" if is_wanxiang else "tokens.json"
    args.table = args.table or (WANXIANG / table_name if is_wanxiang else ROOT / "release/拼音反查码表/拼音.txt")
    args.tokens = args.tokens or (WANXIANG / tokens_name if is_wanxiang else ARCHIVE / "experiments/joint-pinyin-20260921/tokens.json")
    resources = {"joint_3gram.klm": args.model, tokens_name: args.tokens, table_name: args.table}
    if args.rerank_model: resources["joint5-q8.klm"] = args.rerank_model
    for name, source in resources.items():
        if digest(source) != FROZEN[name]: raise ValueError(f"Not the frozen resource: {source}")
    files = {
        "TigerClaw.Core.exe": OUT / f"Aot-{args.arch}/TigerClaw.Core.exe",
        "jointkenlm.dll": OUT / f"{args.arch}/jointkenlm.dll",
        "TigerClaw.Overlay.exe": OUT / f"Overlay-{args.arch}/TigerClaw.Overlay.exe",
        "Win32/TigerClaw.dll": OUT / "TSF-Win32/TigerClaw.dll",
        "TigerClaw.Dialog.exe": OUT / f"Dialog-{args.arch}/TigerClaw.Dialog.exe",
        "TigerClaw.Dialog.exe.config": OUT / f"Dialog-{args.arch}/TigerClaw.Dialog.exe.config",
        "TigerClaw.Shared.dll": OUT / f"Dialog-{args.arch}/TigerClaw.Shared.dll",
    }
    if args.arch == "ARM64":
        files.update({"TigerClaw.dll": OUT / "Wrapper/arm64x_wrapper/TigerClaw.dll",
                      "TigerClawARM64.dll": OUT / "TSF-ARM64/TigerClaw.dll",
                      "TigerClawx64.dll": OUT / "TSF-x64/TigerClaw.dll"})
    else:
        files["x64/TigerClaw.dll"] = OUT / "TSF-x64/TigerClaw.dll"
    files["安装.bat"] = ROOT / ("dist_arm64_install.bat" if args.arch == "ARM64" else "dist_install.bat")
    files["卸载.bat"] = ROOT / ("dist_arm64_uninstall.bat" if args.arch == "ARM64" else "dist_uninstall.bat")
    expected = 0xaa64 if args.arch == "ARM64" else 0x8664
    for name, source in files.items():
        if not source.is_file(): raise FileNotFoundError(source)
        architecture = 0x14c if name.startswith("Win32/") else 0x8664 if name in ("TigerClawx64.dll", "x64/TigerClaw.dll") else expected
        if name in ("TigerClaw.Core.exe", "jointkenlm.dll", "TigerClaw.Overlay.exe", "TigerClaw.dll", "TigerClawARM64.dll", "TigerClawx64.dll", "x64/TigerClaw.dll", "Win32/TigerClaw.dll") and machine(source) != architecture:
            raise ValueError(f"Wrong architecture: {source}")
    target.mkdir(parents=True)
    for name, source in files.items():
        (target / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target / name)
    scheme = target / "码表/虎爪全拼"
    (scheme / "resources").mkdir(parents=True)
    shutil.copy2(ROOT / "next/Schemas/虎爪全拼/schema.json", scheme / "schema.json")
    for name, source in resources.items():
        destination = scheme / "resources" / name
        # Buffered copy avoids large-file fast-copy issues on WSL/DrvFS.
        with source.open("rb") as src, destination.open("xb") as dst:
            shutil.copyfileobj(src, dst, 1024 * 1024)
            dst.flush(); os.fsync(dst.fileno())
        if digest(destination) != FROZEN[name]: raise ValueError(f"Copied resource hash mismatch: {destination}")
    # Plain data stays replaceable. Include original English sources, license
    # and pinned provenance for the selected Chinese lexicon and unchanged model.
    shutil.copytree(ROOT / "third_party/rime-ice-english", scheme / "resources/english")
    shutil.copy2(ROOT / "rime/tiger_sentence/tiger_sentence.codes.txt", scheme / "resources/tiger-codes.txt")
    double_scheme = target / "码表/虎爪小鹤双拼"
    double_scheme.mkdir()
    shutil.copy2(ROOT / "next/Schemas/虎爪小鹤双拼/schema.json", double_scheme / "schema.json")
    for descriptor in (scheme / "schema.json", double_scheme / "schema.json"):
        data = json.loads(descriptor.read_text(encoding="utf-8"))
        data.update(lexicon="resources/" + table_name, tokens="resources/" + tokens_name,
                    lexicon_provider="wanxiang-base-v18.0.8" if is_wanxiang else "tiger-original-65120")
        if args.rerank_model:
            data.update(rerank_model="resources/joint5-q8.klm", reranker="joint-fivegram-top50-v1")
        descriptor.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if is_wanxiang:
        for name in ("wanxiang-pinyin.manifest.json", "wanxiang-NOTICE.md", "wanxiang-dictionary-sources.zip"):
            shutil.copy2(WANXIANG / name, scheme / "resources" / name)
    def link_or_copy(source, destination):
        try: os.link(source, destination)
        except OSError: shutil.copy2(source, destination)
        return destination
    shutil.copytree(scheme / "resources", double_scheme / "resources", copy_function=link_or_copy)
    (target / "config.txt").write_text("码表存储位置\t码表\n当前码表\t虎爪全拼\n整句神经重排\t否\n全拼纠正学习\t是\n全拼简拼\t是\n全拼拼写兼容\t是\n全拼错拼纠正\t否\n拼音英文候选\t是\n拼音表情候选\t是\n", encoding="utf-8-sig")
    for directory in [OUT / f"Overlay-{args.arch}/sounds"]:
        shutil.copytree(directory, target / directory.name)
    shutil.copy2(ROOT / "docs/FULL_PINYIN.md", target / "README.md")
    shutil.copy2(ROOT / "next/TigerClaw.Overlay.Native/THIRD-PARTY-NOTICES.txt", target / "Overlay-THIRD-PARTY-NOTICES.txt")
    with zipfile.ZipFile(target / "kenlm-source.zip", "w", zipfile.ZIP_DEFLATED) as archive:
        for subtree in ["third_party/kenlm", "next/TigerClaw.Pinyin.Native"]:
            for path in (ROOT / subtree).rglob("*"):
                if path.is_file(): archive.write(path, path.relative_to(ROOT))
    manifest = {
        "arch": args.arch, "engine": "full_pinyin", "llm": False, "dictionary": args.dictionary,
        "git_commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
        "working_tree_changes": True, "installed": False,
        "source_sha256": {str(p.relative_to(ROOT)): digest(p) for folder in ["next/TigerClaw.Pinyin", "next/TigerClaw.Core", "next/TigerClaw.Shared", "next/TigerClaw.Dialog", "next/TigerClaw.Overlay.Native", "BimeTSF2/SampleIME"]
                          for p in sorted((ROOT / folder).rglob("*")) if p.is_file() and p.suffix in (".cs", ".cpp", ".h", ".xaml", ".csproj", ".vcxproj") and not any(x in p.parts for x in ("obj", "bin", "Release", "Debug"))},
        "files": {str(p.relative_to(target)): digest(p) for p in sorted(target.rglob("*")) if p.is_file()},
    }
    (target / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(target)

if __name__ == "__main__": main()
