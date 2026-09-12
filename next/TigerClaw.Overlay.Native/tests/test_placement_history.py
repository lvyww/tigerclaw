"""Compile production geometry and failure-specific negative controls.

Use --build-dir for a retained isolated build directory (no deletion). Otherwise
scratch cleanup retries sharing violations without masking the real test result.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def run(cxx, build, sanitize):
    compiler = shutil.which(cxx)
    if not compiler:
        raise RuntimeError(f'Compiler not found: {cxx}')
    msvc = Path(compiler).name.lower() in ('cl', 'cl.exe')
    if msvc and sanitize:
        raise ValueError('ASan/UBSan requires GCC or Clang')
    flags = ['/nologo', '/std:c++17', '/EHsc', '/utf-8', '/W4', '/O2'] if msvc else [
        '-std=c++17', '-Wall', '-Wextra', '-Wpedantic', '-Werror', '-O2']
    if sanitize:
        flags += ['-O1', '-g', '-fsanitize=address,undefined', '-fno-omit-frame-pointer']
    header = (ROOT / 'Placement.h').read_text(encoding='utf-8')
    source = ROOT / 'tests/PlacementHistoryTests.cpp'
    variants = [
        ('production', header, None),
        ('no-memory', header.replace('above_ && evidence_ != 0 && fitsAbove', 'false'),
         'above inheritance before expiry'),
        ('self-renewal', header.replace('reason = PlacementReason::InheritedAbove;',
                                       'reason = PlacementReason::OverflowAbove;'),
         'inherited above incorrectly renewed evidence'),
        ('no-expiry', header.replace('if (records_[next_].reason == PlacementReason::OverflowAbove) --evidence_;',
                                    '// Deliberately fail to age evidence.'),
         'evidence must expire on decision 101'),
        ('reset-on-hide', header.replace('void EndInput() { anchored_ = false; decisionValid_ = false; }',
                                        'void EndInput() { Reset(); }'),
         'above inheritance before expiry'),
    ]
    for name, text, expected in variants:
        if expected and text == header:
            raise RuntimeError(f'Update negative control: {name}')
        directory = build / name
        directory.mkdir(parents=True, exist_ok=True)
        (directory / 'Placement.h').write_text(text, encoding='utf-8')
        exe = directory / ('probe.exe' if os.name == 'nt' else 'probe')
        include = [f'/I{directory}'] if msvc else ['-I', str(directory)]
        output = [f'/Fe:{exe}'] if msvc else ['-o', str(exe)]
        subprocess.run([compiler, *flags, *include, str(source), *output],
                       cwd=directory, check=True, timeout=180)
        result = subprocess.run([str(exe)], cwd=directory, capture_output=True, text=True, timeout=60)
        if expected:
            if result.returncode != 1 or expected not in result.stderr:
                raise RuntimeError(f'{name} not rejected for intended reason: {result}')
            print(json.dumps({'negative_control': name, 'status': 'passed'}), flush=True)
        else:
            if result.returncode:
                raise RuntimeError(f'Production geometry failed: {result.stderr}')
            print(result.stdout, end='', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cxx', default=os.environ.get('CXX', 'g++'))
    parser.add_argument('--sanitize', action='store_true')
    parser.add_argument('--build-dir', type=Path)
    args = parser.parse_args()
    build = args.build_dir.resolve() if args.build_dir else Path(tempfile.mkdtemp(prefix='tigerclaw-history-'))
    build.mkdir(parents=True, exist_ok=True)
    try:
        run(args.cxx, build, args.sanitize)
    finally:
        if not args.build_dir:
            for attempt in range(6):
                try:
                    shutil.rmtree(build)
                    break
                except FileNotFoundError:
                    break
                except OSError as error:
                    if attempt == 5:
                        import sys
                        print(json.dumps({'cleanup_warning': str(error), 'directory': str(build),
                                          'test_result_unchanged': True}), file=sys.stderr)
                    else:
                        time.sleep(0.1 * (2 ** attempt))


if __name__ == '__main__':
    main()
