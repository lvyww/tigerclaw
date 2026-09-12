"""Compile production candidate-direction code, with failure-specific controls.

Portable: python tools/test_candidate_orientation.py
Windows: add --windows-ui --platform x64 (or x86); --protocol tests real Core.
All build/intermediate files live in a bounded-cleanup temporary directory.
No installation, input injection, user configuration or release directories.
"""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
RETRIES = (0.1, 0.2, 0.4, 0.8, 1.6)


@contextmanager
def scratch():
    directory = tempfile.TemporaryDirectory(prefix='tigerclaw-orientation-')
    try:
        yield Path(directory.name)
    finally:
        for attempt in range(len(RETRIES) + 1):
            try:
                directory.cleanup()
                break
            except OSError as error:
                if attempt < len(RETRIES):
                    time.sleep(RETRIES[attempt])
                else:
                    print(json.dumps({'phase': 'cleanup', 'status': 'warning',
                                      'directory': directory.name, 'error': str(error),
                                      'test_result_unchanged': True}), file=sys.stderr)


def command(args, *, timeout=300, check=True):
    result = subprocess.run([str(x) for x in args], cwd=ROOT, capture_output=True,
                            text=True, encoding='utf-8', errors='replace', timeout=timeout)
    print(result.stdout, end='', flush=True)
    if result.stderr:
        print(result.stderr, end='', file=sys.stderr, flush=True)
    if check:
        result.check_returncode()
    return result


def build(dotnet, project, work, name, properties=()):
    artifacts = work / name
    command([dotnet, 'build', ROOT / project, '-c', 'Release', '--artifacts-path', artifacts,
             '--nologo', '-v:minimal', *properties], timeout=600)
    # Restrict discovery to runnable bin outputs; never execute a ref/obj assembly.
    assembly = Path(project).stem
    suffix = '.exe' if assembly == 'TigerClaw.Overlay.Tests' else '.dll'
    matches = list((artifacts / 'bin' / assembly).rglob(assembly + suffix))
    if len(matches) != 1:
        raise RuntimeError(f'Expected one test assembly under {artifacts}: {matches}')
    return matches[0]


def require_control(result, message):
    if result.returncode != 1 or message not in result.stderr:
        raise RuntimeError(f'Negative control not rejected for expected reason: {result}')
    print(json.dumps({'negative_control': 'passed', 'detected': message}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--windows-ui', action='store_true')
    parser.add_argument('--protocol', action='store_true')
    parser.add_argument('--platform', choices=['x64', 'x86'], default='x64')
    args = parser.parse_args()
    dotnet = shutil.which('dotnet')
    if not dotnet:
        parser.error('.NET 10 SDK required')
    if (args.windows_ui or args.protocol) and os.name != 'nt':
        parser.error('WPF / real Core protocol checks require Windows')
    with scratch() as work:
        project = 'next/TigerClaw.Candidate.Tests/TigerClaw.Candidate.Tests.csproj'
        executable = build(dotnet, project, work, 'geometry')
        command([dotnet, executable])
        source = (ROOT / 'next/TigerClaw.Overlay/CandidateOrientation.cs').read_text(encoding='utf-8')
        gate = 'if (!(_above && fitsAbove))'
        if source.count(gate) != 1:
            raise RuntimeError('Update geometry negative-control gate')
        control = work / 'OrientationControl.cs'
        control.write_text(source.replace(gate, 'if (true)'), encoding='utf-8')
        executable = build(dotnet, project, work, 'geometry-control',
                           [f'-p:CandidateOrientationSource={control}'])
        require_control(command([dotnet, executable], check=False),
                        'above memory lost across short compositions')
        if args.protocol:
            executable = build(dotnet, project, work, 'protocol',
                               ['-p:CandidateProtocolTests=true', '-p:PublishAot=false'])
            command([dotnet, executable], timeout=120)
        if args.windows_ui:
            project = 'next/TigerClaw.Overlay.Tests/TigerClaw.Overlay.Tests.csproj'
            platform = f'-p:PlatformTarget={args.platform}'
            executable = build(dotnet, project, work, 'windows-ui', [platform])
            command([executable, ROOT], timeout=120)
            source = (ROOT / 'next/TigerClaw.Overlay/CandidateWindowPositioner.cs').read_text(encoding='utf-8')
            gate = '            _hasCaretAnchor = false;'
            if source.count(gate) != 1:
                raise RuntimeError('Update UI negative-control gate')
            control = work / 'PositionerControl.cs'
            control.write_text(source.replace(gate, gate + '\n            _orientation.Reset();'), encoding='utf-8')
            executable = build(dotnet, project, work, 'windows-ui-control',
                               [platform, f'-p:CandidatePositionerSource={control}'])
            require_control(command([executable, ROOT], timeout=120, check=False),
                            'inherited first frame positioned below')
    print(json.dumps({'status': 'passed', 'suite': 'candidate-orientation',
                      'windows_ui': args.windows_ui, 'platform': args.platform,
                      'protocol': args.protocol, 'physical_input_tested': False}), flush=True)


if __name__ == '__main__':
    main()
