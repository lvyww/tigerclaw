"""Exercise the publisher's actual input resolver and copy helper in isolated cmd fixtures."""
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def win(path):
    return subprocess.check_output(['wslpath', '-w', str(path)], text=True).strip()


def main():
    source = (ROOT / 'publish.bat').read_text()
    helpers = source[source.index('\n:ResolveSentenceInputs') + 1:]
    assert source.index('call :ResolveSentenceInputs') < source.index('[2/13]')
    with tempfile.TemporaryDirectory(prefix='publish inputs ', dir=ROOT / 'next/_run') as tmp:
        root = Path(tmp)
        def put(name, text='fixture'):
            p = root / name
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(text)
            return p
        shape = put('shape.bin')
        put('third_party/llama.cpp/LICENSE')
        model = put('release/sentence/Models/sentence-qwen-q8.gguf', 'retained model')
        license = put('release/sentence/licenses/Qwen3-LICENSE.txt', 'retained license')
        canonical = put('canonical/model.gguf', 'canonical model')
        canonical_license = put('canonical/LICENSE', 'canonical license')
        def run(model_path, license_path, explicit_model=False, explicit_license=False, no_qwen=False, tail=''):
            batch = root / 'run fixture.bat'
            lines = ['@echo off', 'setlocal EnableExtensions', f'set "ROOT={win(root)}"',
                     f'set "SENTENCE_NGRAM_MODEL={win(shape)}"',
                     f'set "SENTENCE_QWEN_MODEL={win(model_path)}"',
                     f'set "SENTENCE_QWEN_LICENSE={win(license_path)}"',
                     'set "TIGERCLAW_QWEN_MODEL=' + ('explicit' if explicit_model else '') + '"',
                     'set "TIGERCLAW_QWEN_LICENSE=' + ('explicit' if explicit_license else '') + '"',
                     f'set "PUBLISH_NO_QWEN={int(no_qwen)}"',
                     'call :ResolveSentenceInputs', 'if errorlevel 1 exit /b 1',
                     'echo RESOLVED_MODEL=%SENTENCE_QWEN_MODEL%', 'echo RESOLVED_LICENSE=%SENTENCE_QWEN_LICENSE%',
                     tail, 'exit /b %errorlevel%', helpers]
            batch.write_bytes(('\r\n'.join(lines).replace('\r\n', '\n').replace('\n', '\r\n')).encode())
            return subprocess.run(['cmd.exe', '/d', '/c', win(batch)], capture_output=True)
        missing = root / 'missing'
        r = run(canonical, canonical_license)
        assert r.returncode == 0 and win(canonical).encode() in r.stdout, r.stdout + r.stderr
        r = run(missing, missing)
        assert r.returncode == 0 and win(model).encode() in r.stdout and win(license).encode() in r.stdout, r.stdout + r.stderr
        r = run(canonical, canonical_license, True, True)
        assert r.returncode == 0 and win(canonical).encode() in r.stdout
        assert run(missing, canonical_license, explicit_model=True).returncode != 0
        assert run(canonical, missing, explicit_license=True).returncode != 0
        # Same source/destination, including normalized relative paths and case.
        same = win(model.parent / '..' / 'Models' / model.name).upper()
        r = run(missing, missing, tail=f'call :CopyFileStrict "{win(model)}" "{same}"')
        assert r.returncode == 0 and model.read_text() == 'retained model', r.stdout + r.stderr
        target = root / 'copied model.gguf'
        r = run(missing, missing, tail=f'call :CopyFileStrict "{win(model)}" "{win(target)}"')
        assert r.returncode == 0 and target.read_bytes() == model.read_bytes()
        r = run(missing, missing, tail=f'call :CopyFileStrict "{win(missing)}" "{win(missing)}"')
        assert r.returncode != 0, 'missing same-path source must fail'
        license.unlink()
        assert run(missing, missing).returncode != 0
        model.unlink()
        assert run(missing, canonical_license).returncode != 0
        assert run(missing, missing, no_qwen=True).returncode == 0
        shape.unlink()
        assert run(missing, missing, no_qwen=True).returncode != 0
    print('Publish input resolution and same-file copy: all checks passed')


if __name__ == '__main__':
    main()
