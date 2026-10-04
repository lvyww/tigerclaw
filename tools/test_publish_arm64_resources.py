"""Exercise the actual ARM64 batch resource helpers in an isolated Windows folder."""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ResourceTests(unittest.TestCase):
    def test_resolution_and_copy(self):
        text = (ROOT / 'publish_arm64.bat').read_text()
        helpers = text[text.index(':ResolveSentenceResources\n'):text.index(':FindMsbuild\n')]
        stop_hook = next(line for line in text.splitlines() if 'Get-Process TigerClaw ' in line)
        with tempfile.TemporaryDirectory(prefix='arm64 resources ', dir='/mnt/c/Users/yc/AppData/Local/Temp') as tmp:
            work = Path(tmp)
            release = work / 'release'
            model = release / 'sentence/Models/sentence-qwen-q8.gguf'
            license = release / 'sentence/licenses/Qwen3-LICENSE.txt'
            for p in (model, license):
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text('fixture')
            def win(p):
                return subprocess.check_output(['wslpath', '-w', str(p)], text=True).strip()
            def execute(extra='', copy=''):
                setup = ('@echo off\r\nsetlocal\r\n'
                         'set "TIGERCLAW_QWEN_MODEL="\r\n'
                         'set "TIGERCLAW_QWEN_LICENSE="\r\n'
                         f'set "RELEASE_DIR={win(release)}"\r\n'
                         f'set "SENTENCE_QWEN_MODEL={win(work / "missing-model")}"\r\n'
                         f'set "SENTENCE_QWEN_LICENSE={win(work / "missing-license")}"\r\n')
                body = setup + stop_hook + '\r\n' + extra + 'call :ResolveSentenceResources\r\nif errorlevel 1 exit /b 1\r\n' + copy + 'exit /b 0\r\n' + helpers.replace('\n', '\r\n')
                batch = work / 'probe.bat'
                batch.write_bytes(body.encode())
                return subprocess.run(['cmd.exe', '/d', '/c', win(batch)], capture_output=True)
            # Missing canonical paths fall back to existing installed resources;
            # source==destination must neither truncate nor fail.
            before = model.read_bytes()
            result = execute(copy=f'call :CopyUnlessSame "{win(model)}" "{win(model)}"\r\nif errorlevel 1 exit /b 1\r\n')
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertEqual(model.read_bytes(), before)
            # Explicit invalid overrides must fail, never silently use an old file.
            result = execute(f'set "TIGERCLAW_QWEN_MODEL={win(work / "absent")}"\r\n')
            self.assertNotEqual(result.returncode, 0)
            other = work / 'external.gguf'
            other.write_text('new fixture')
            result = execute(f'set "TIGERCLAW_QWEN_MODEL={win(other)}"\r\n',
                             f'call :CopyUnlessSame "%SENTENCE_QWEN_MODEL%" "{win(model)}"\r\nif errorlevel 1 exit /b 1\r\n')
            self.assertEqual(result.returncode, 0, result.stdout)
            self.assertEqual(model.read_bytes(), other.read_bytes())
            license.unlink()
            self.assertNotEqual(execute().returncode, 0)


if __name__ == '__main__':
    unittest.main()
