import unittest
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from export_wanxiang import syllable, han


class ExportTests(unittest.TestCase):
    def test_tones_and_umlaut(self):
        for source, expected in [('nǚ', 'nv'), ('lǜ', 'lv'), ('lüè', 'lue'),
                                 ('nüè', 'nue'), ('shàng', 'shang'), ('ḿ', 'm'),
                                 ('m̀', 'm'), ('ń', 'n'), ('yī', 'yi')]:
            self.assertEqual(syllable(source), expected)

    def test_never_interpret_non_pinyin(self):
        for value in ['𫚪', 'ni3', "xi'an", '', 'nǚ er']:
            with self.assertRaises(ValueError):
                syllable(value)

    def test_han_boundary(self):
        self.assertTrue(han('女儿𠀀〇'))
        for value in ['', 'QQ', '中 国', '你好！', 'A股']:
            self.assertFalse(han(value))

    def test_export_imports_dedup_and_bad_source_records(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'dicts').mkdir()
            (root / 'wanxiang.dict.yaml').write_text('import_tables:\n  - dicts/zi\n')
            (root / 'dicts/zi.dict.yaml').write_text(
                '---\nname: zi\n...\n女\tnǚ\t2\n女\tnǜ\t3\n'
                '女儿\tnǚ ér\t5\n略\tlüè\t-1\n缺\tquē\n'
                '异\tyì\t10s\n坏\t坏\t1\nA股\ta gǔ\t1\n')
            out = root / 'out.txt'
            subprocess.run([sys.executable, str(Path(__file__).with_name('export_wanxiang.py')),
                            str(root), str(out)], check=True, capture_output=True)
            self.assertEqual(out.read_text().splitlines(), [
                '女\tnv\t3', '女儿\tnver\t5', '略\tlue\t0', '缺\tque\t0', '异\tyi\t0'])
            counts = json.loads(out.with_suffix('.manifest.json').read_text())['counts']
            self.assertEqual(counts['sourceRows'], 8)
            self.assertEqual(counts['exportedRows'], 5)
            self.assertEqual(counts['non_han_text'], 1)
            self.assertEqual(counts['non_pinyin_code'], 1)

    def test_runtime_tokens_use_explicit_source_readings(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); (root / 'dicts').mkdir()
            (root / 'wanxiang.dict.yaml').write_text('import_tables:\n  - dicts/zi\n')
            (root / 'dicts/zi.dict.yaml').write_text('...\n银行卡\tyín háng kǎ\t20\n虐\tnüè\t1\n略\tlüè\t2\n花儿\thuār\t5\n')
            subprocess.run([sys.executable, str(Path(__file__).with_name('export_wanxiang.py')), str(root),
                            str(root/'words.txt'), '--tokens', str(root/'tokens.json')], check=True, capture_output=True)
            data = json.loads((root/'tokens.json').read_text())
            self.assertEqual([r['tokens'] for r in data], [['银/yin', '行/hang', '卡/ka'], ['虐/nve'], ['略/lve']])
            self.assertEqual(json.loads((root/'words.manifest.json').read_text())['counts']['unaligned_source_syllables'], 1)


if __name__ == '__main__':
    unittest.main()
