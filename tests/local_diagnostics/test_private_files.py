"""Non-secret temporary files only; no credentials or external processes."""
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from local_diagnostics.private_files import write_private_json_exclusive, write_private_text_exclusive


@unittest.skipUnless(os.name == 'posix', 'Private descriptor permissions require POSIX')
class PrivateFileContracts(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory(prefix='diagnostic-file-synthetic-')
        self.addCleanup(folder.cleanup)
        self.q = Path(folder.name)

    def test_text_round_trip_is_utf8_and_private(self):
        path = self.q / 'sentinel'
        write_private_text_exclusive(path, '\u4ec5\u6d4b\u8bd5 non-secret sentinel\n')
        self.assertEqual(path.read_text(encoding='utf-8'), '\u4ec5\u6d4b\u8bd5 non-secret sentinel\n')
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)

    def test_permissions_are_exact_under_different_umasks(self):
        for index, mask in enumerate([0, 0o077, 0o777]):
            path = self.q / str(index)
            previous = os.umask(mask)
            try:
                write_private_text_exclusive(path, 'synthetic')
            finally:
                os.umask(previous)
            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)

    def test_existing_evidence_is_not_overwritten_or_chmodded(self):
        path = self.q / 'existing'
        path.write_text('original evidence')
        path.chmod(0o640)
        with self.assertRaises(FileExistsError):
            write_private_text_exclusive(path, 'replacement')
        self.assertEqual(path.read_text(), 'original evidence')
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o640)

    def test_symlinks_including_dangling_links_are_not_followed(self):
        target = self.q / 'target'
        target.write_text('original')
        for name, destination in [('link', target), ('dangling', self.q / 'absent')]:
            link = self.q / name
            link.symlink_to(destination)
            with self.assertRaises(FileExistsError):
                write_private_text_exclusive(link, 'replacement')
        self.assertEqual(target.read_text(), 'original')
        self.assertFalse((self.q / 'absent').exists())

    def test_json_round_trip_is_private(self):
        path = self.q / 'document.json'
        value = {'sentinel': '\u4ec5\u79bb\u7ebf\u6587\u4ef6\u9a8c\u8bc1', 'sealed': False}
        write_private_json_exclusive(path, value)
        self.assertEqual(json.loads(path.read_text(encoding='utf-8')), value)
        self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)

    def test_non_text_rejects_before_creating_a_file(self):
        path = self.q / 'not-created'
        with self.assertRaises(TypeError):
            write_private_text_exclusive(path, object())
        self.assertFalse(path.exists())

    def test_json_serialization_error_rejects_before_file_creation(self):
        path = self.q / 'not-created.json'
        with self.assertRaises(TypeError):
            write_private_json_exclusive(path, {'unsupported': object()})
        self.assertFalse(path.exists())


if __name__ == '__main__':
    unittest.main()
