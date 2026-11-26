import os
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[2] / "scripts/archive_publication.py"
publish_archive = runpy.run_path(str(SCRIPT))["publish_archive"]


class ArchivePublicationTests(unittest.TestCase):
    def test_a_late_zip_collision_preserves_old_files_and_rolls_back_new_companions(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            staging = output / "staging"
            staging.mkdir()
            archive, manifest, digest = [staging / n for n in ("delivery.zip", "manifest.json", "digest.sha256")]
            for file in (archive, manifest, digest):
                file.write_bytes(file.name.encode())
            historical = output / manifest.name
            historical.write_bytes(manifest.read_bytes())
            original = historical.stat()
            link = os.link

            def concurrent_link(source, destination):
                if source == archive:
                    destination.write_bytes(b"another completed delivery")
                return link(source, destination)

            with patch.object(os, "link", side_effect=concurrent_link):
                with self.assertRaises(FileExistsError):
                    publish_archive(output, archive, [manifest, digest])

            self.assertTrue(os.path.samestat(original, historical.stat()))
            self.assertEqual(historical.read_bytes(), manifest.read_bytes())
            self.assertFalse((output / digest.name).exists())
            self.assertEqual((output / archive.name).read_bytes(), b"another completed delivery")

    def test_dangling_companion_symlink_prevents_publication(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            staging = output / "staging"
            staging.mkdir()
            archive, manifest = staging / "delivery.zip", staging / "manifest.json"
            archive.write_bytes(b"new ZIP")
            manifest.write_bytes(b"new manifest")
            destination = output / manifest.name
            destination.symlink_to(output / "absent")

            with self.assertRaises(FileExistsError):
                publish_archive(output, archive, [manifest])

            self.assertTrue(destination.is_symlink())
            self.assertFalse((output / archive.name).exists())
            self.assertFalse((output / "absent").exists())
