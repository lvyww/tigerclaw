"""Preserve a source baseline for renderer comparisons without touching runtime."""
from pathlib import Path
import hashlib
import json
import shutil
import uuid
root = Path(__file__).resolve().parents[2]
out = root / 'next/_run/OverlayRenderBench' / uuid.uuid4().hex[:12]
source = root / 'next/TigerClaw.Overlay.Native'
shutil.copytree(source, out / 'source')
cmake = out / 'source/CMakeLists.txt'
vendor = str(root / 'third_party/llama.cpp/vendor')
if vendor.startswith('/mnt/c/'):
    vendor = 'C:/' + vendor[len('/mnt/c/'):]
cmake.write_text(cmake.read_text().replace('../../third_party/llama.cpp/vendor', '"' + vendor + '"'))
(out / 'source-hashes.json').write_text(json.dumps({p.name: hashlib.sha256(p.read_bytes()).hexdigest()
    for p in source.glob('*') if p.is_file()}, indent=2))
print(out)
