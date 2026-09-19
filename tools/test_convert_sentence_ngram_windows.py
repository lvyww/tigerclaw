import struct
import tempfile
import unittest
from pathlib import Path

from convert_sentence_ngram_mobile import convert as to_mobile
from convert_sentence_ngram_windows import convert as to_windows


class ConversionTests(unittest.TestCase):
    def test_roundtrip_and_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, mobile, restored = [root / n for n in ('source', 'mobile', 'restored')]
            # Includes sparse-index boundaries, supplementary Unicode, a zero
            # observed bigram and an empty trigram context with nontrivial backoff.
            chars = [0, 2, 3, *range(0x4e00, 0x4e20), 0x20000]
            unigrams = [(c, 0.01) for c in chars]
            contexts = [(c, 0.7) for c in chars]
            bigrams = [((c << 21) | 0x20000, 0.0 if c == 2 else 0.1) for c in chars]
            tc = [((2 << 21) | 0x4e00, 0.3), ((0x20000 << 21) | 0x20000, 0.8)]
            trigrams = [((tc[0][0] << 21) | 0x20000, 0.2)]
            data = bytearray(struct.pack('<8sI', b'TCSKNM01', 1))
            for records, count_format, record_format in [(unigrams,'I','If'), (bigrams,'Q','Qf'),
                    (contexts,'I','If'), (trigrams,'Q','Qf'), (tc,'Q','Qf')]:
                data.extend(struct.pack('<' + count_format, len(records)))
                for record in records:
                    data.extend(struct.pack('<' + record_format, *record))
            source.write_bytes(data)
            to_mobile(source, mobile, 16)
            to_windows(mobile, restored)
            self.assertEqual(source.read_bytes(), restored.read_bytes())
            with self.assertRaises(FileExistsError):
                to_windows(mobile, restored)
            self.assertEqual(source.read_bytes(), restored.read_bytes())
            damaged = root / 'damaged'
            damaged.write_bytes(mobile.read_bytes()[:-1])
            with self.assertRaises(ValueError):
                to_windows(damaged, root / 'rejected')
            self.assertFalse((root / 'rejected').exists())


if __name__ == '__main__':
    unittest.main()
