"""Stage instrumented source copies; never edits production sources or runtime."""
import hashlib
import argparse
import json
from pathlib import Path
import shutil
import uuid

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--single-poll', action='store_true', help='Unsafe diagnostic control only: remove cross-poll snapshot confirmation in staged native copy')
args = parser.parse_args()
OUT = ROOT / 'next/_run/OverlayLatency' / uuid.uuid4().hex[:12]
OUT.mkdir(parents=True)

def copy_tree(name):
    shutil.copytree(ROOT / 'next' / name, OUT / name,
                    ignore=shutil.ignore_patterns('bin', 'obj', '.vs'))

def replace(path, old, new):
    data = path.read_text(encoding='utf-8-sig')
    if data.count(old) != 1:
        raise ValueError(f'Expected exactly one patch site: {path}: {old}')
    path.write_text(data.replace(old, new), encoding='utf-8')

for name in ['TigerClaw.Overlay', 'TigerClaw.Shared', 'TigerClaw.Overlay.Native']:
    copy_tree(name)
shutil.copy2(ROOT / 'next/app.manifest', OUT / 'app.manifest')
native = OUT / 'TigerClaw.Overlay.Native'
wpf = OUT / 'TigerClaw.Overlay'
shared = OUT / 'TigerClaw.Shared'
session = 'Latency-' + OUT.name
constants = (shared / 'RuntimeConstants.cs').read_text()
for old, new in {
    'Local\\TigerClaw.UiState.v1': f'Local\\TigerClaw.Overlay.Test.{session}.Ui',
    'Local\\TigerClaw.Heartbeat.v1': f'Local\\TigerClaw.Overlay.Test.{session}.Core',
    'Local\\TigerClaw.OverlayHeartbeat.v1': f'Local\\TigerClaw.Overlay.Test.{session}.Overlay',
    'Local\\TigerClaw.ShowMenu.v1': f'Local\\TigerClaw.Overlay.Test.{session}.Menu',
    'BimeIPC': f'TigerClaw.Overlay.Test.{session}',
}.items():
    constants = constants.replace(old, new)
(shared / 'RuntimeConstants.cs').write_text(constants)
# No registration check, process-name guard, or production window titles in this copy.
(wpf / 'App.xaml.cs').write_text('namespace TigerClaw.Overlay { public partial class App : System.Windows.Application {} }')
replace(wpf / 'TigerClaw.Overlay.csproj', '<AssemblyName>TigerClaw.Overlay</AssemblyName>', '<AssemblyName>TigerClaw.Overlay.Wpf.Latency</AssemblyName>')
replace(wpf / 'MainWindow.xaml', 'Title="TigerClawCandidate"', 'Title="TigerClaw Latency WPF"')
replace(wpf / 'StatusWindow.xaml', 'Title="TigerStatusOverlay"', 'Title="TigerClaw Latency WPF Status"')
(wpf / 'Bench.cs').write_text('''using System.Diagnostics;
using System.IO.MemoryMappedFiles;
namespace TigerClaw.Overlay {
internal static class Bench {
    static readonly MemoryMappedFile Map = MemoryMappedFile.OpenExisting(@"Local\\TigerClaw.Overlay.Test.SESSION.Ack");
    static readonly MemoryMappedViewAccessor View = Map.CreateViewAccessor();
    internal static void Done(long seq, long read, long ui) {
        long done = Stopwatch.GetTimestamp();
        View.Write(8, read); View.Write(16, ui); View.Write(24, done);
        System.Threading.Thread.MemoryBarrier(); View.Write(0, seq);
    }
}}
'''.replace('SESSION', session))
replace(wpf / 'MainWindow.xaml.cs', 'private void OnStateChanged(OverlayUiState uiState, long uiSeq, long tick64)\n        {',
        'private void OnStateChanged(OverlayUiState uiState, long uiSeq, long tick64)\n        {\n            long benchRead = System.Diagnostics.Stopwatch.GetTimestamp();')
replace(wpf / 'MainWindow.xaml.cs', '_state = uiState ?? new OverlayUiState();',
        'long benchUi = System.Diagnostics.Stopwatch.GetTimestamp();\n                    _state = uiState ?? new OverlayUiState();')
replace(wpf / 'MainWindow.xaml.cs', 'ApplyUiState(changes);', 'ApplyUiState(changes);\n                    Bench.Done(uiSeq, benchRead, benchUi);')

(native / 'Bench.h').write_text('''#pragma once
#include <windows.h>
#include <atomic>
namespace Bench {
inline std::atomic<long long> sequence{0}, received{0};
inline long long Now() { LARGE_INTEGER n; QueryPerformanceCounter(&n); return n.QuadPart; }
inline void Done(long long ui) {
    auto done = Now();
    static HANDLE map = OpenFileMappingW(FILE_MAP_WRITE, FALSE, L"Local\\\\TigerClaw.Overlay.Test.SESSION.Ack");
    static auto view = static_cast<volatile long long*>(map ? MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, 32) : nullptr);
    if (!view) return;
    view[1] = received.load(); view[2] = ui; view[3] = done;
    MemoryBarrier(); view[0] = sequence.load();
}
}
'''.replace('SESSION', session))
replace(native / 'Transport.cpp', '#include "Transport.h"', '#include "Transport.h"\n#include "Bench.h"')
replace(native / 'Transport.cpp', 'lastSequence = seq; lastTick = tick; haveSnapshot = false;',
        'lastSequence = seq; lastTick = tick; haveSnapshot = false;\n            Bench::received = Bench::Now(); Bench::sequence = seq;')
if args.single_poll:
    replace(native / 'Transport.cpp', '''            if (!haveSnapshot || previousSnapshot != payload)
            {
                previousSnapshot = std::move(payload); haveSnapshot = true; continue;
            }
''', '            // UNSAFE diagnostic control: not eligible for deployment.\n')
replace(native / 'main.cpp', '#include "Transport.h"', '#include "Transport.h"\n#include "Bench.h"')
replace(native / 'main.cpp', 'if (source_ && source_->Take(state_))\n                {',
        'if (source_ && source_->Take(state_))\n                {\n                    auto benchUi = Bench::Now();')
replace(native / 'main.cpp', 'Refresh(false);\n                }\n                return 0;\n            case ReplyMessage:',
        'Refresh(false);\n                    Bench::Done(benchUi);\n                }\n                return 0;\n            case ReplyMessage:')
# CMake relative vendor path no longer resolves from the isolated staging folder.
cmake = (native / 'CMakeLists.txt').read_text()
vendor = str(ROOT / 'third_party/llama.cpp/vendor')
if vendor.startswith('/mnt/c/'):
    vendor = 'C:/' + vendor[len('/mnt/c/'):]
(native / 'CMakeLists.txt').write_text(cmake.replace('../../third_party/llama.cpp/vendor', '"' + vendor + '"'))
manifest = dict(session=session, staged=str(OUT), single_poll_diagnostic=args.single_poll, source_hashes={})
for folder in ['TigerClaw.Overlay', 'TigerClaw.Shared', 'TigerClaw.Overlay.Native']:
    for path in (ROOT / 'next' / folder).rglob('*'):
        if path.is_file() and path.suffix in ('.cs', '.cpp', '.h', '.xaml') and not {'bin', 'obj'}.intersection(path.parts):
            manifest['source_hashes'][str(path.relative_to(ROOT))] = hashlib.sha256(path.read_bytes()).hexdigest()
(OUT / 'manifest.json').write_text(json.dumps(manifest, indent=2))
print(OUT)
