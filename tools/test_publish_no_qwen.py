"""WSL integration check using Windows cmd/7-Zip and isolated release fixtures."""
import pathlib
import shutil
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def win(path):
    return subprocess.check_output(['wslpath', '-w', str(path)], text=True).strip()


def main():
    parent = ROOT / 'next' / '_run'
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='no-qwen-test-', dir=parent) as temp:
        root = pathlib.Path(temp)
        shutil.copy2(ROOT / 'pack_release.bat', root)
        shutil.copy2(ROOT / 'publish_no_qwen.bat', root)
        # Exercise the real wrapper and failure propagation without publishing.
        (root / 'publish.bat').write_bytes(b'@echo off\r\nif not "%~1"=="--no-qwen" exit /b 9\r\nexit /b 7\r\n')
        result = subprocess.run(['cmd.exe', '/d', '/c', win(root / 'publish_no_qwen.bat')], capture_output=True)
        assert result.returncode == 7, result.stdout + result.stderr
        release = root / 'release'
        release.mkdir()
        for name in ('7z.exe', '7z.dll'):
            source = ROOT / name
            if not source.exists():
                source = ROOT / 'release' / name
            shutil.copy2(source, release / name)
        files = ['jointkenlm.dll', 'Models/sentence-fivegram.klm', 'licenses/kenlm/LICENSE', 'TigerClaw.Core.exe', 'TigerClaw.Overlay.exe', 'Overlay-THIRD-PARTY-NOTICES.txt',
                 'TigerClaw.Dialog.exe', 'TigerClaw.Dialog.exe.config', 'TigerClaw.exe',
                 'TigerClaw.Shared.dll', 'bime.ico', '更新日志.txt', '安装.bat', '卸载.bat',
                 '自定义选重键.txt', 'x64/TigerClaw.dll', 'Win32/TigerClaw.dll',
                 'Models/sentence-ngram-v2.bin', 'Models/sentence-ngram-mobile.bin',
                 'sentence/Models/old/sentence-ngram-mobile.bin', 'Models/sentence-qwen-q8.gguf',
                 'sentence/TigerClaw.Sentence.exe', 'sentence/Models/sentence-qwen-q8.gguf',
                 'sentence/Models/old/QWEN.GGUF', 'sentence/licenses/llama.cpp-LICENSE.txt',
                 'sounds/KeyNormal.wav', '字体/font.txt', '拼音反查码表/table.txt', '码表/test.txt']
        for name in files:
            path = release / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('fixture', encoding='utf-8')
        table = root / 'release_arm64' / '码表' / '虎整句' / '虎整句.txt'
        table.parent.mkdir(parents=True)
        table.write_text('fixture', encoding='utf-8')
        (root / 'dist_config.txt').write_text('distribution config', encoding='utf-8')
        (release / 'config.txt').write_text('private config', encoding='utf-8')
        for no_qwen in (True, False):
            args = ['cmd.exe', '/d', '/c', win(root / 'pack_release.bat'), '1.2.3']
            if no_qwen:
                args.append('--no-qwen')
            result = subprocess.run(args, capture_output=True)
            assert result.returncode == 0, (result.stdout + result.stderr).decode(errors='replace')
            archive = release / ('虎爪输入法-1.2.3' + ('-no-qwen' if no_qwen else '') + '.7z')
            listing = subprocess.check_output([str(release / '7z.exe'), 'l', '-slt', win(archive)]).decode(errors='replace')
            assert ('.gguf' in listing.lower()) == (not no_qwen)
            assert 'sentence-ngram-mobile.bin' not in listing
            for name in ('sentence-ngram-v2.bin', 'sentence-fivegram.klm', 'jointkenlm.dll', 'TigerClaw.Sentence.exe', 'llama.cpp-LICENSE.txt'):
                assert name in listing, name
            assert (release / 'TigerClaw' / 'config.txt').read_text() == 'distribution config'
            subprocess.run([str(release / '7z.exe'), 't', win(archive)], check=True, capture_output=True)
        assert (release / 'Models/sentence-qwen-q8.gguf').read_text() == 'fixture'
        assert (release / 'Models/sentence-ngram-mobile.bin').read_text() == 'fixture'
        # A fivegram without its scoring DLL must not become a silent trigram package.
        dll = release / 'jointkenlm.dll'
        dll.rename(dll.with_suffix('.saved'))
        result = subprocess.run(['cmd.exe', '/d', '/c', win(root / 'pack_release.bat'), 'missing-native'], capture_output=True)
        assert result.returncode != 0
        assert not list(release.glob('*missing-native*.7z'))
        dll.with_suffix('.saved').rename(dll)
        # A stale mobile model must not silently substitute for the chosen format.
        model = release / 'Models/sentence-ngram-v2.bin'
        model.rename(model.with_suffix('.saved'))
        result = subprocess.run(['cmd.exe', '/d', '/c', win(root / 'pack_release.bat'), 'missing-model'], capture_output=True)
        assert result.returncode != 0
        assert not list(release.glob('*missing-model*.7z'))
        assert table.read_text() == 'fixture'
        assert len(list(release.glob('*.7z'))) == 2
    print('No-Qwen/full packaging, stale models, wrapper failure propagation and CRC checks passed.')


if __name__ == '__main__':
    main()
