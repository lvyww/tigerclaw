from pathlib import Path
import base64, hashlib, lzma, re, subprocess
parts = [Path('.learning-transfer-' + str(i)) for i in range(4)]
patch = lzma.decompress(base64.b64decode(''.join(p.read_text().strip() for p in parts), validate=True))
assert hashlib.sha256(patch).hexdigest() == '103ac102615b78faffc4ba868c6e0d673455fc5f27b22166b580f91ad8f62516', 'Payload digest mismatch'
blocks = []
entries = []
for raw in patch.split(b'diff --git ')[1:]:
    block = b'diff --git ' + raw
    match = re.match(rb'diff --git a/(\S+) b/(\S+)\n', block)
    assert match and match[1] == match[2], 'Unexpected patch path'
    path = match[1].decode('utf-8')
    assert not path.startswith('/') and '..' not in Path(path).parts
    if path.startswith('.github/workflows/'):
        print('CI workflow will be installed separately through authenticated connector:', path)
        continue
    index = re.search(rb'^index ([0-9a-f]+)\.\.([0-9a-f]+)', block, re.M)
    assert index, path
    before, after = [s.decode() for s in index.groups()]
    if set(before) == {'0'}:
        assert not Path(path).exists(), 'New file already exists: ' + path
    else:
        actual = subprocess.check_output(['git','hash-object',path],text=True).strip()
        assert actual.startswith(before), 'Base content changed: ' + path
    entries.append((path, after)); blocks.append(block)
subprocess.run(['git','apply','--index','--unidiff-zero','--whitespace=nowarn','-'],input=b''.join(blocks),check=True)
for path, expected in entries:
    actual = subprocess.check_output(['git','hash-object',path],text=True).strip()
    assert actual.startswith(expected), 'Applied content mismatch: ' + path
    print('VERIFIED', path, actual)
for p in parts: p.unlink()
Path(__file__).unlink()
print('Verified feature files:',len(entries))
