"""Compile broken Core variants in scratch copies; require specific assertion rejection.

Requires a Release Core.Tests build. Never edits checkout sources or user runtime.
Windows: python tools/test_sentence_review_faults.py
WSL: pass --dotnet '/mnt/c/Program Files/dotnet/dotnet.exe'.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
D = 'SentenceInputDecoder.cs'
C = 'SentenceInputDecoder.Cache.cs'
MUTATIONS = [
    ('confidence-menu', D, '                    result,\n                    confidenceTruncated,',
     '                    visible,\n                    confidenceTruncated,', 'hidden candidate denominator'),
    ('ancestor-truncation', D, 'InheritTruncation(ancestorTruncated)', 'InheritTruncation(false)', 'ancestor truncation propagated'),
    ('consumer-alias', D, 'copy.EarlyCommitEvidence = EarlyCommitEvidence?.Copy();\n            return copy;', 'copy.EarlyCommitEvidence = EarlyCommitEvidence?.Copy();\n            return this;', 'evidence consumer cannot mutate cached snapshot'),
    ('cooperative-cancel', C, '            _cancellation.ThrowIfCancellationRequested();', '            // fault: no cancellation', 'cooperative cancel'),
    ('history-release', C, '            if (floor - _cachedHistoryFloor < 64) return;', '            return; // fault: retain history', 'old history positions'),
    ('journal-window', 'SentenceLearningStore.cs', 'Events.Where(e => !Removed.Contains(e.Id)).TakeLast(MaximumEvents)',
     'Events.TakeLast(MaximumEvents).Where(e => !Removed.Contains(e.Id))', 'tombstones applied before combined window'),
]
MUTATIONS += [
    ('locked-cache', C, 'if (!ReferenceEquals(_cachedLockedPrefix, locked))',
     'if (locked != null || !ReferenceEquals(_cachedLockedPrefix, locked))', 'identical locked request'),
    ('whole-code-boundary', C, 'oldLength > _maxCodeLength + TrailingSelectorSpan(old ?? string.Empty)',
     'oldLength > 0', 'candidate count'),
]


def compiler_path(path: Path) -> str:
    if sys.platform == 'linux' and str(path).startswith('/mnt/'):
        return subprocess.check_output(['wslpath', '-w', str(path)], text=True).strip()
    return str(path)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        parser.error('dotnet SDK unavailable')
    binaries = ROOT / 'next/_run/Tests/Release/net10.0-windows'
    if not (binaries / 'TigerClaw.Core.Tests.dll').exists():
        parser.error('Build Release Core.Tests first')
    parent = ROOT / 'next/_run/review-validation'
    parent.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix='faults-', dir=parent))
    try:
        for directory in ['TigerClaw.Core', 'TigerClaw.Shared']:
            shutil.copytree(ROOT / 'next' / directory, work / 'next' / directory,
                            ignore=shutil.ignore_patterns('obj', 'bin', '.tmpobj'))
        shutil.copy2(ROOT / 'next/app.manifest', work / 'next/app.manifest')
        shutil.copytree(binaries, work / 'runner')
        project = work / 'next/TigerClaw.Core/TigerClaw.Core.csproj'
        for name, filename, before, after, expected in MUTATIONS:
            source = work / 'next/TigerClaw.Core' / filename
            original = source.read_bytes()
            text = original.decode('utf-8').replace('\r\n', '\n')
            assert text.count(before) == 1, (name, 'mutation anchor changed')
            try:
                source.write_text(text.replace(before, after), encoding='utf-8')
                build = subprocess.run([dotnet, 'build', compiler_path(project), '-c', 'Release',
                                        '-p:PublishAot=false', '--nologo'], cwd=work,
                                       capture_output=True, timeout=180, check=True)
                (parent / f'fault-{name}-build.log').write_bytes(build.stdout + build.stderr)
                shutil.copy2(work / 'next/_run/Release/net10.0-windows/TigerClaw.Core.dll', work / 'runner/TigerClaw.Core.dll')
                result = subprocess.run([dotnet, compiler_path(work / 'runner/TigerClaw.Core.Tests.dll'),
                                         '--sentence-review-tests'], cwd=work,
                                        capture_output=True, timeout=90)
                output = (result.stdout + result.stderr).decode('utf-8', errors='replace')
                (parent / f'fault-{name}.log').write_text(output, encoding='utf-8')
                assert result.returncode == 1 and expected in output, (name, result.returncode, output[-2000:])
                print(json.dumps({'fault': name, 'status': 'rejected', 'assertion': expected}), flush=True)
            finally:
                source.write_bytes(original)
        print(json.dumps({'test': 'sentence_review_faults', 'status': 'passed', 'variants': len(MUTATIONS)}))
    finally:
        for attempt in range(6):
            try:
                shutil.rmtree(work)
                break
            except OSError as error:
                if attempt < 5:
                    time.sleep(.1 * (2 ** attempt))
                    continue
                print(json.dumps({'phase': 'cleanup', 'status': 'warning', 'directory': str(work),
                                  'error': str(error), 'test_result_unchanged': True}), file=sys.stderr)


if __name__ == '__main__':
    main()
