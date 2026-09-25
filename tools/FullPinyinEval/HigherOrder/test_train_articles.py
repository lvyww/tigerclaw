"""Small independent corpus fixture: leakage, dedup, boundaries and archives."""
import argparse
import io
import json
from pathlib import Path
import tarfile
import tempfile
import zipfile
from train_articles import normalized, document_split, digest, prepare
from build_articles_model import verified_copy


def main():
    assert normalized('标题：测试\n你好，世界！https://example.com/广告\n作者:某人'.encode(), 'x.txt')[0] == ['你好','世界']
    assert normalized('你好世界'.encode('gb18030'), 'x.txt')[1] == 'gb18030'
    assert normalized('你好世界'.encode('utf-16'), 'x.txt')[1] == 'utf-16'
    assert normalized('你好世界。你好世界'.encode(), 'x.txt')[0] == ['你好世界']
    assert normalized('a你b中国c'.encode(), 'x.txt')[0] == ['中国']
    with tempfile.TemporaryDirectory(prefix='articles-training-test-') as directory:
        root = Path(directory)
        original=root/'original';original.write_bytes(b'a'*(4*1024*1024)+b'bcdef')
        dest=root/'copied';partial=root/'copied.partial'
        partial.write_bytes(b'a'*(4*1024*1024)+b'WRONG')
        repaired=verified_copy(original,dest)
        assert dest.read_bytes()==original.read_bytes() and len(repaired['repaired_blocks'])==1
        assert verified_copy(original,dest)['repaired_blocks']==[]
        corpus = root/'source.tar'
        rows = {'pu/reserved.txt':'留出文本。共有片段', 'pu/copy.txt':'留出文本。共有片段',
                'pu/training.txt':'训练正文。共有片段', 'en/skip.txt':'不能加入'}
        # Enough deterministic synthetic documents to exercise each hash split.
        for i in range(12000):
            text='独立语料'+chr(0x4e00+i%100)+chr(0x4e00+i//100)+'甲乙丙丁戊己庚辛壬癸'
            rows[f'pu/{i}.txt']=text
        with tarfile.open(corpus,'w') as archive:
            for name,text in rows.items():
                data=text.encode();info=tarfile.TarInfo(name);info.size=len(data);archive.addfile(info,io.BytesIO(data))
        essays=root/'essays.zip'
        with zipfile.ZipFile(essays,'w') as archive: archive.writestr('readme.py','ignored')
        poetry=root/'poetry';poetry.mkdir()
        provenance=root/'provenance.jsonl'
        provenance.write_text(json.dumps(dict(source_file='/mnt/c/Archive/Copus/articles/pu/reserved.txt'))+'\n')
        output=root/'output'
        prepare(argparse.Namespace(snapshot=corpus,poetry=poetry,essays=essays,provenance=provenance,output=output))
        train=set((output/'train.tokens').read_text().splitlines())
        dev=set((output/'dev.tokens').read_text().splitlines());test=set((output/'test.tokens').read_text().splitlines())
        assert not train&dev and not train&test and not dev&test
        assert ' '.join('留出文本') not in train and ' '.join('共有片段') not in train
        assert ' '.join('不能加入') not in train
        assert ' '.join('训练正文') in train
        assert document_split(b'\0'*32)==2 and document_split(b'\0'*32,True)==3
    print('Articles corpus isolation tests passed')


if __name__=='__main__':main()
