"""Compile the exact Native Overlay placement policy; require failing controls."""
import argparse
import json
import os
from pathlib import Path
import shutil
from test_candidate_orientation import ROOT, command, scratch


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cxx', default=os.environ.get('CXX', 'g++'))
    parser.add_argument('--sanitize', action='store_true')
    args = parser.parse_args()
    compiler = shutil.which(args.cxx)
    if not compiler:
        parser.error('C++ compiler not found: ' + args.cxx)
    msvc = Path(compiler).name.lower() in {'cl', 'cl.exe'}
    if args.sanitize and msvc:
        parser.error('Use Clang/GCC for --sanitize')
    source = ROOT / 'next/TigerClaw.Overlay.Native'
    with scratch() as work:
        flags = ['/nologo', '/std:c++17', '/EHsc', '/utf-8', '/O2', '/W4'] if msvc else [
            '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror']
        if args.sanitize:
            flags += ['-O1', '-g', '-fsanitize=address,undefined', '-fno-omit-frame-pointer']
        def run(include, name):
            exe = work / (name + ('.exe' if os.name == 'nt' else ''))
            options = [f'/I{include}', f'/Fe:{exe}', f'/Fo:{work / (name + ".obj")}'] if msvc else ['-I', include, '-o', exe]
            command([compiler, *flags, source / 'tests/OrientationTests.cpp', *options])
            return command([exe], check=False)
        result = run(source, 'orientation')
        result.check_returncode()
        production = (source / 'Placement.h').read_text(encoding='utf-8')
        for name, gate, replacement in [
            ('stateless', 'if (!(above_ && fitsAbove))', 'if (true)'),
            ('composition_reset', 'void EndComposition() { anchored_ = false; }',
             'void EndComposition() { anchored_ = false; above_ = false; placed_ = false; }')]:
            if production.count(gate) != 1:
                raise RuntimeError('Update negative-control gate: ' + name)
            directory = work / name; directory.mkdir()
            (directory / 'Placement.h').write_text(production.replace(gate, replacement), encoding='utf-8')
            result = run(directory, name + '-probe')
            expected = 'above memory lost across short compositions'
            if result.returncode != 1 or expected not in result.stderr:
                raise RuntimeError('Wrong negative-control outcome: ' + str(result))
            print(json.dumps({'negative_control': name, 'status': 'passed', 'specific_failure': expected}), flush=True)


if __name__ == '__main__':
    main()
