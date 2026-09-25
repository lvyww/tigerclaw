"""Build and verify a Rime pack from authoritative source and the pinned Q8 model."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
PACK = ROOT / 'rime/tiger_sentence'


def sha(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--threads', type=int, choices=range(1, 13), default=4)
    args = parser.parse_args()
    expected = json.loads((PACK / 'default-model.json').read_text())
    assert args.model.stat().st_size == expected['bytes']
    assert sha(args.model) == expected['sha256'], 'Unexpected default model'
    assert not list(PACK.glob('*.bin')), 'Top-level binary resources are unsafe during Rime deployment'
    assert not (PACK / 'tiger_sentence.options.yaml').exists(), 'Personal options must not be distributed'
    assert (PACK / 'models/tiger_sentence.lexical.bin').is_file()
    assert not args.output.exists(), 'Choose a new versioned output; preserve previous releases'
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='tiger-rime-package-') as temporary:
        temp = Path(temporary)
        package = temp / 'package'
        shutil.copytree(PACK, package)
        shutil.copy2(ROOT / 'LICENSE', package / 'LICENSE')
        shutil.copyfile(args.model, package / 'models/sentence-fivegram-mobile.bin')
        assert sha(package / 'models/sentence-fivegram-mobile.bin') == expected['sha256']
        assert not (package / 'tools').exists()
        (package / '安装说明.txt').write_text(
            '虎整句 Rime 三模型 Q8 主线：部署清理修复版（2026-09-25）\n\n'
            '备份用户目录后，同时更新本包 Lua、方案和 models/，再重新部署。\n'
            '保留个人 custom 补丁、码表、tiger_sentence.options.yaml 和学习数据。\n'
            '词汇辅助文件必须位于 models/tiger_sentence.lexical.bin；顶层旧副本可由部署清理移入 trash。\n'
            '不要只移动文件而继续使用旧 Lua。新版兼容读取旧顶层路径，但不从 trash 自动恢复。\n'
            '五阶模型仍为405.66 MB三模型TCSKNM03 Q8，模型内容及融合权重未改变。\n', encoding='utf-8')
        files = {str(p.relative_to(package)): {'bytes': p.stat().st_size, 'sha256': sha(p)}
                 for p in sorted(package.rglob('*')) if p.is_file()}
        # Ensure copy integrity for every authoritative runtime resource.
        for p in PACK.rglob('*'):
            if p.is_file():
                assert sha(p) == files[str(p.relative_to(PACK))]['sha256']
        manifest = {'model_sha256': expected['sha256'], 'lexical_path': 'models/tiger_sentence.lexical.bin',
                    'files': files}
        (package / 'PACKAGE-MANIFEST.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
        archive = temp / 'release.7z'
        subprocess.run(['7z', 'a', '-t7z', '-mx=5', f'-mmt={args.threads}', str(archive), '.'], cwd=package, check=True)
        subprocess.run(['7z', 't', str(archive)], check=True)
        extract = temp / 'verify'
        subprocess.run(['7z', 'x', '-y', str(archive), '-o' + str(extract)], check=True)
        actual = {str(p.relative_to(extract)) for p in extract.rglob('*') if p.is_file()}
        assert actual == set(files) | {'PACKAGE-MANIFEST.json'}
        assert json.loads((extract / 'PACKAGE-MANIFEST.json').read_text()) == manifest
        for name, item in files.items():
            p = extract / name
            assert p.stat().st_size == item['bytes'] and sha(p) == item['sha256'], name
        digest = sha(archive)
        partial = args.output.with_suffix(args.output.suffix + '.partial')
        for attempt in range(3):
            with archive.open('rb') as src, partial.open('wb') as dst:
                shutil.copyfileobj(src, dst, 8 * 1024 * 1024)
                dst.flush()
                os.fsync(dst.fileno())
            if sha(partial) == digest:
                break
        else:
            raise RuntimeError('Archive copy hash mismatch; original release not replaced')
        partial.replace(args.output)
        args.output.with_suffix(args.output.suffix + '.sha256').write_text(digest + '  ' + args.output.name + '\n')
        args.output.with_suffix(args.output.suffix + '.manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
        print(json.dumps({'output': str(args.output), 'bytes': args.output.stat().st_size,
                          'sha256': digest, 'crc': 'passed', 'extracted_files': len(actual)}, ensure_ascii=False))


if __name__ == '__main__':
    main()
