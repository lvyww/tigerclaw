import importlib.util, json, pathlib, subprocess, sys, tempfile, unittest
ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('learning_records', ROOT / 'tools' / 'learning_records.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
class MaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = pathlib.Path(self.temp.name)
        self.path = self.root / '自学习-虎爪.txt'
        self.events = [dict(id=f'id-{i}', time=1000+i, mode='test-v1', code='aa', text='虎𰻞', context='前文',levels=i+1) for i in range(3)]
        self.path.write_bytes(b''.join(m.event_line(e) for e in self.events))
    def tearDown(self): self.temp.cleanup()
    def test_unicode_roundtrip(self): self.assertEqual(m.parse(self.path.read_bytes())[0], self.events)
    def test_read_does_not_change(self):
        old = self.path.read_bytes(); self.assertEqual(m.maintain(self.path, 'show')['count'], 3); self.assertEqual(old, self.path.read_bytes())
    def test_undo_exact(self):
        m.maintain(self.path, 'undo'); self.assertEqual(m.parse(self.path.read_bytes())[0], self.events[:-1])
    def test_undo_then_replay(self):
        m.maintain(self.path, 'undo'); self.path.write_bytes(self.path.read_bytes() + m.event_line(self.events[-1])); self.assertEqual(len(m.parse(self.path.read_bytes())[0]), 2)
    def test_clear_retains_tombstones(self):
        m.maintain(self.path, 'clear'); self.path.write_bytes(self.path.read_bytes()+b''.join(m.event_line(e) for e in self.events)); self.assertEqual(m.parse(self.path.read_bytes())[0], [])
    def test_import_idempotent(self):
        source=self.root/'export.json'; source.write_text(json.dumps({'format':m.FORMAT,'events':self.events}),encoding='utf-8')
        m.maintain(self.path,'import',source); self.assertEqual(len(m.parse(self.path.read_bytes())[0]),3)
    def test_import_all_or_nothing(self):
        old=self.path.read_bytes(); source=self.root/'bad.json'; source.write_text(json.dumps({'format':m.FORMAT,'events':[dict(self.events[0], id='new'),{}]}),encoding='utf-8')
        with self.assertRaises(ValueError):m.maintain(self.path,'import',source)
        self.assertEqual(self.path.read_bytes(),old)
    def test_corrupt_hand_edit_is_preserved(self):
        damaged=self.path.read_bytes()+b'invalid row\n'
        self.path.write_bytes(damaged)
        with self.assertRaises(ValueError):m.maintain(self.path,'compact')
        self.assertEqual(self.path.read_bytes(),damaged)
    def test_readable_levels(self):
        data=self.path.read_text(encoding='utf-8')
        self.assertIn('虎𰻞',data);self.assertIn('前文',data);self.assertIn('学习',data)
        self.assertEqual(m.summary(self.events)[0]['跨上下文等级'],6)
    def test_single_undo_removes_three_levels(self):
        m.maintain(self.path,'undo')
        self.assertEqual(m.summary(m.parse(self.path.read_bytes())[0])[0]['跨上下文等级'],3)
    def test_old_format_is_rejected(self):
        with self.assertRaises(ValueError):m.parse(b'TCL1\tE\told\t123\n')
    def test_reject_code_table_name(self):
        for name in ['词库.txt','自学习-虎爪.txt.bak.txt','scheme.dict.yaml']:
            with self.assertRaises(ValueError): m.checked_path(str(self.root/name))
    def test_missing_show_no_files(self):
        self.path.unlink(); self.assertEqual(m.maintain(self.path,'show')['count'],0);self.assertFalse(self.path.exists());self.assertFalse(pathlib.Path(str(self.path)+'.lock').exists())
    def test_confirmation_required(self):
        old=self.path.read_bytes(); run=subprocess.run([sys.executable,str(ROOT/'tools'/'learning_records.py'),str(self.path),'clear'],capture_output=True)
        self.assertNotEqual(run.returncode,0);self.assertEqual(old,self.path.read_bytes())
    def test_export_no_overwrite(self):
        dest=self.root/'backup.json';dest.write_text('keep')
        run=subprocess.run([sys.executable,str(ROOT/'tools'/'learning_records.py'),str(self.path),'export','--file',str(dest)],capture_output=True)
        self.assertNotEqual(run.returncode,0);self.assertEqual(dest.read_text(),'keep')
if __name__=='__main__':unittest.main(verbosity=2)
