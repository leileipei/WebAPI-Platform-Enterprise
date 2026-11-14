"""Non-secret local files and decisions only; no runtime adapters."""
import importlib.util
import os
from pathlib import Path
import stat
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
if importlib.util.find_spec('local_diagnostics.output_bounds'):
    from local_diagnostics import output_bounds as bounds
else:
    bounds = None


class OutputContracts(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory(prefix='output-bounds-synthetic-')
        self.addCleanup(folder.cleanup)
        self.root = Path(folder.name).resolve()

    def api(self):
        self.assertIsNotNone(bounds, 'offline output primitives are not implemented')
        return bounds

    def machine(self):
        return self.api().StopMachine({'work': 8}, {'guest': 4}, started_at=0,
                                      work_seconds=10, total_seconds=12, stale_seconds=1)

    def capture(self, machine=None, soft=6, cap=8):
        capture = self.api().LogCapture(self.root, machine or self.machine(),
                                        soft_bytes=soft, limit_bytes=cap, chunk_bytes=8)
        self.addCleanup(lambda: capture.finish(eof=False))
        return capture

    def source(self, raw=b'<trx/>'):
        directory = self.root / 'input'
        directory.mkdir(mode=0o700)
        file = directory / 'source.trx'
        file.write_bytes(raw)
        file.chmod(0o600)
        return directory, file

    def copy(self, directory, machine=None, limit=8, soft=6):
        return self.api().copy_trx(directory, 'source.trx', self.root, 'copied.trx',
                                   machine or self.machine(), limit_bytes=limit,
                                   soft_bytes=soft, chunk_bytes=2)

    def test_usage_boundary_latches_and_cannot_recover(self):
        m = self.machine()
        m.observe(0.1, 0.1, {'work': 8}, {'guest': 10})
        m.observe(0.2, 0.2, {'work': 0}, {'guest': 100})
        self.assertEqual(m.snapshot()['primary'], 'usage_limit')
        self.assertFalse(m.snapshot()['may_advance'])

    def test_free_reserve_boundary_stops(self):
        m = self.machine()
        m.observe(0.1, 0.1, {'work': 0}, {'guest': 4})
        self.assertEqual(m.snapshot()['primary'], 'reserve_risk')

    def test_unknown_and_stale_measurements_fail_closed(self):
        for now, sampled, usage in [(0.1, 0.1, {}), (2, 0, {'work': 0}),
                                     (0.1, 0.1, {'work': None}), (0.1, 0.2, {'work': 0})]:
            with self.subTest(now=now, sampled=sampled, usage=usage):
                m = self.machine()
                m.observe(now, sampled, usage, {'guest': 10})
                self.assertFalse(m.snapshot()['may_advance'])

    def test_nan_negative_bool_and_backwards_clock_do_not_pass(self):
        for now, sampled, usage in [(float('nan'), 0, 0), (0.1, 0.1, -1),
                                    (0.1, 0.1, False), (-1, -1, 0)]:
            m = self.machine()
            m.observe(now, sampled, {'work': usage}, {'guest': 10})
            self.assertFalse(m.snapshot()['may_advance'])

    def test_work_and_total_deadlines_preserve_first_failure(self):
        m = self.machine()
        m.trip('business_failure')
        m.observe(10, 10, {'work': 0}, {'guest': 10})
        m.observe(12, 12, {'work': 0}, {'guest': 10})
        value = m.snapshot()
        self.assertEqual(value['primary'], 'business_failure')
        self.assertIn('work_deadline', value['secondary'])
        self.assertIn('total_deadline', value['secondary'])

    def test_finalized_machine_does_not_reopen_or_grant_admission(self):
        m = self.machine()
        m.finalize(evidence_complete=True)
        m.observe(0, 0, {'work': 0}, {'guest': 10})
        self.assertFalse(m.snapshot()['may_advance'])
        self.assertEqual(m.snapshot()['outcome'], 'observation_only')

    def test_invalid_reason_does_not_echo_arbitrary_text(self):
        m = self.machine()
        with self.assertRaisesRegex(ValueError, '^INVALID_STOP_REASON$'):
            m.trip('SECRET_SENTINEL')
        self.assertNotIn('SECRET_SENTINEL', str(m.snapshot()))

    def test_huge_integer_time_is_rejected_without_leaving_machine_open(self):
        m = self.machine()
        m.observe(10 ** 400, 0, {'work': 0}, {'guest': 10})
        self.assertEqual(m.snapshot()['primary'], 'clock_invalid')
        self.assertFalse(m.snapshot()['may_advance'])

    def test_utf8_bytes_share_cap_across_channels(self):
        m = self.machine()
        c = self.capture(m)
        c.feed('stdout', '\u4e2d'.encode('utf-8'))
        c.feed('stderr', b'abc')
        self.assertEqual(m.snapshot()['primary'], 'log_soft_limit')
        c.feed('stdout', b'defg')
        value = c.finish(eof=True)
        self.assertEqual(value['bytes_written'], 8)
        self.assertEqual(value['omitted_observed_bytes'], 2)
        self.assertFalse(value['complete'])
        self.assertEqual((self.root / 'stdout').read_bytes(), '\u4e2d'.encode('utf-8') + b'de')
        self.assertEqual((self.root / 'stderr').read_bytes(), b'abc')

    def test_exact_ceiling_conservatively_marks_incomplete(self):
        c = self.capture()
        c.feed('stdout', b'12345678')
        self.assertFalse(c.finish(eof=True)['complete'])

    def test_eof_required_and_finish_idempotent(self):
        c = self.capture()
        c.feed('stdout', b'abc')
        first = c.finish(eof=False)
        self.assertIsNone(first['omitted_total_bytes'])
        self.assertFalse(first['complete'])
        self.assertEqual(c.finish(eof=True), first)
        with self.assertRaisesRegex(RuntimeError, 'CAPTURE_CLOSED'):
            c.feed('stdout', b'x')

    def test_under_limit_eof_is_durable_and_private(self):
        c = self.capture()
        c.feed('stdout', b'abc')
        value = c.finish(eof=True)
        self.assertTrue(value['complete'])
        self.assertTrue(value['durable'])
        self.assertEqual(stat.S_IMODE((self.root / 'stdout').stat().st_mode), 0o600)

    def test_existing_log_and_symlink_are_never_overwritten(self):
        original = self.root / 'stdout'
        original.write_bytes(b'historical')
        original.chmod(0o640)
        c = self.capture()
        c.feed('stdout', b'new')
        self.assertFalse(c.finish(eof=True)['complete'])
        self.assertEqual(original.read_bytes(), b'historical')
        self.assertEqual(stat.S_IMODE(original.stat().st_mode), 0o640)

    def test_path_escape_and_oversize_chunk_reject_before_write(self):
        c = self.capture()
        for name, raw in [('../escape', b'x'), ('bad/name', b'x'), ('stdout', b'123456789')]:
            with self.assertRaises(ValueError):
                c.feed(name, raw)
        self.assertEqual(list(self.root.iterdir()), [])

    def test_symlink_directory_and_nonprivate_directory_rejected(self):
        link = self.root / 'alias'
        link.symlink_to(self.root, target_is_directory=True)
        for directory in [link, self.root]:
            if directory == self.root:
                self.root.chmod(0o755)
            with self.assertRaises((OSError, ValueError)):
                self.api().LogCapture(directory, self.machine(), soft_bytes=6, limit_bytes=8)
        self.root.chmod(0o700)

    def test_short_writes_complete_once_without_losing_bytes(self):
        c = self.capture()
        real_write = os.write
        with patch.object(os, 'write', side_effect=lambda fd, data: real_write(fd, data[:1])):
            c.feed('stdout', b'abcd')
        self.assertTrue(c.finish(eof=True)['complete'])
        self.assertEqual((self.root / 'stdout').read_bytes(), b'abcd')

    def test_write_error_preserves_partial_bytes_and_safe_reason(self):
        m = self.machine()
        c = self.capture(m)
        real_write = os.write
        count = [0]
        def fail_after_prefix(fd, data):
            count[0] += 1
            if count[0] == 1:
                return real_write(fd, data[:2])
            raise OSError('SECRET_SENTINEL')
        with patch.object(os, 'write', side_effect=fail_after_prefix):
            c.feed('stdout', b'abcd')
        result = c.finish(eof=True)
        self.assertEqual(result['bytes_written'], 2)
        self.assertFalse(result['complete'])
        self.assertEqual((self.root / 'stdout').read_bytes(), b'ab')
        self.assertNotIn('SECRET_SENTINEL', str(result) + str(m.snapshot()))

    def test_io_failure_is_not_misclassified_as_a_capacity_ceiling(self):
        m = self.machine()
        c = self.capture(m)
        with patch.object(os, 'write', side_effect=OSError('synthetic')):
            c.feed('stdout', b'abc')
        c.finish(eof=True)
        self.assertNotIn('log_ceiling', m.snapshot()['secondary'])

    def test_replaced_log_path_never_confirms_the_new_name_as_complete(self):
        c = self.capture()
        c.feed('stdout', b'ab')
        (self.root / 'stdout').rename(self.root / 'retained-prefix')
        (self.root / 'stdout').write_bytes(b'replacement')
        result = c.finish(eof=True)
        self.assertFalse(result['complete'])
        self.assertEqual((self.root / 'stdout').read_bytes(), b'replacement')
        self.assertEqual((self.root / 'retained-prefix').read_bytes(), b'ab')

    def test_linked_log_is_not_extended_after_identity_change(self):
        c = self.capture()
        c.feed('stdout', b'ab')
        os.link(self.root / 'stdout', self.root / 'alias')
        c.feed('stdout', b'cd')
        self.assertFalse(c.finish(eof=True)['complete'])
        self.assertEqual((self.root / 'alias').read_bytes(), b'ab')

    def test_same_length_log_rewrite_is_not_confirmed(self):
        c = self.capture()
        c.feed('stdout', b'abcd')
        (self.root / 'stdout').write_bytes(b'WXYZ')
        self.assertFalse(c.finish(eof=True)['complete'])

    def test_log_change_during_final_directory_sync_is_not_confirmed(self):
        c = self.capture()
        c.feed('stdout', b'abcd')
        real_sync = os.fsync
        def sync_and_change(fd):
            if stat.S_ISDIR(os.fstat(fd).st_mode):
                (self.root / 'stdout').write_bytes(b'WXYZ')
            return real_sync(fd)
        with patch.object(os, 'fsync', side_effect=sync_and_change):
            self.assertFalse(c.finish(eof=True)['complete'])

    def test_initial_log_symlink_does_not_write_its_target(self):
        target = self.root / 'historical'
        target.write_bytes(b'original')
        (self.root / 'stdout').symlink_to(target)
        c = self.capture()
        c.feed('stdout', b'new')
        self.assertFalse(c.finish(eof=True)['complete'])
        self.assertEqual(target.read_bytes(), b'original')

    def test_fsync_error_never_reports_durable_success(self):
        m = self.machine()
        c = self.capture(m)
        c.feed('stdout', b'ab')
        with patch.object(os, 'fsync', side_effect=OSError('private error')):
            result = c.finish(eof=True)
        self.assertFalse(result['durable'])
        self.assertFalse(result['complete'])
        self.assertEqual(m.snapshot()['primary'], 'capture_io')

    def test_trx_copy_preserves_source_and_returns_hash(self):
        directory, source = self.source()
        result = self.copy(directory)
        self.assertTrue(result['complete'])
        self.assertEqual(result['bytes_written'], 6)
        self.assertEqual(result['sha256'], '2a0d91a7bbf9ac08ace6b067d08b11ec50ce3491eb0092f63a36cbeb5a1a7267')
        self.assertEqual(source.read_bytes(), b'<trx/>')
        target = self.root / 'copied.trx.partial'
        self.assertEqual(target.read_bytes(), b'<trx/>')
        self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o600)

    def test_trx_too_large_creates_no_destination(self):
        directory, _ = self.source(b'123456789')
        result = self.copy(directory)
        self.assertEqual(result['status'], 'source_too_large')
        self.assertFalse((self.root / 'copied.trx.partial').exists())

    def test_trx_existing_destination_not_overwritten(self):
        directory, _ = self.source()
        target = self.root / 'copied.trx.partial'
        target.write_bytes(b'previous failed copy')
        result = self.copy(directory)
        self.assertFalse(result['complete'])
        self.assertEqual(target.read_bytes(), b'previous failed copy')

    def test_trx_growth_during_read_retains_unconfirmed_prefix(self):
        directory, source = self.source(b'abcdef')
        real_read = os.read
        changed = [False]
        def read_and_grow(fd, size):
            value = real_read(fd, size)
            if not changed[0]:
                changed[0] = True
                with source.open('ab') as stream:
                    stream.write(b'ghijkl')
            return value
        with patch.object(os, 'read', side_effect=read_and_grow):
            result = self.copy(directory)
        self.assertFalse(result['complete'])
        self.assertEqual(result['status'], 'source_changed')
        self.assertLessEqual((self.root / 'copied.trx.partial').stat().st_size, 8)

    def test_trx_same_size_replacement_is_not_confirmed(self):
        directory, source = self.source(b'abcdef')
        replacement = directory / 'replacement'
        replacement.write_bytes(b'UVWXYZ')
        real_read = os.read
        changed = [False]
        def read_and_replace(fd, size):
            value = real_read(fd, size)
            if not changed[0]:
                changed[0] = True
                os.replace(replacement, source)
            return value
        with patch.object(os, 'read', side_effect=read_and_replace):
            result = self.copy(directory)
        self.assertFalse(result['complete'])
        self.assertEqual(result['status'], 'source_changed')
        self.assertEqual(source.read_bytes(), b'UVWXYZ')

    def test_trx_symlink_hardlink_and_fifo_rejected_without_blocking(self):
        directory, source = self.source()
        source.unlink()
        outside = self.root / 'original'
        outside.write_bytes(b'history')
        source.symlink_to(outside)
        self.assertFalse(self.copy(directory)['complete'])
        source.unlink()
        os.link(outside, source)
        self.assertFalse(self.copy(directory)['complete'])
        source.unlink()
        os.mkfifo(source, 0o600)
        self.assertFalse(self.copy(directory)['complete'])
        self.assertEqual(outside.read_bytes(), b'history')

    def test_trx_read_error_and_fsync_error_leave_failed_copy(self):
        directory, _ = self.source()
        with patch.object(os, 'read', side_effect=OSError('SECRET_SENTINEL')):
            result = self.copy(directory)
        self.assertFalse(result['complete'])
        self.assertNotIn('SECRET_SENTINEL', str(result))
        second = self.root / 'second'
        second.mkdir(mode=0o700)
        with patch.object(os, 'fsync', side_effect=OSError('SECRET_SENTINEL')):
            result = self.api().copy_trx(directory, 'source.trx', second, 'copy.trx', self.machine(),
                                         soft_bytes=6, limit_bytes=8)
        self.assertFalse(result['complete'])
        self.assertFalse(result['durable'])

    def test_trx_target_corruption_and_hardlink_are_not_confirmed(self):
        for action in ['truncate', 'rewrite', 'hardlink']:
            with self.subTest(action=action):
                case = self.root / action
                case.mkdir(mode=0o700)
                inputs = case / 'input'
                inputs.mkdir(mode=0o700)
                (inputs / 'source.trx').write_bytes(b'abcdef')
                target = case / 'copy.trx.partial'
                real_sync = os.fsync
                changed = [False]
                def sync_with_mutation(fd):
                    if not changed[0] and target.exists():
                        changed[0] = True
                        if action == 'truncate':
                            target.write_bytes(b'ab')
                        elif action == 'rewrite':
                            target.write_bytes(b'UVWXYZ')
                        else:
                            os.link(target, case / 'alias')
                    return real_sync(fd)
                with patch.object(os, 'fsync', side_effect=sync_with_mutation):
                    result = self.api().copy_trx(inputs, 'source.trx', case, 'copy.trx',
                                                 self.machine(), soft_bytes=6, limit_bytes=8)
                self.assertFalse(result['complete'])

    def test_trx_source_change_during_output_sync_is_not_confirmed(self):
        directory, source = self.source(b'abcdef')
        real_sync = os.fsync
        changed = [False]
        def sync_and_change(fd):
            if not changed[0]:
                changed[0] = True
                source.write_bytes(b'UVWXYZ')
            return real_sync(fd)
        with patch.object(os, 'fsync', side_effect=sync_and_change):
            result = self.copy(directory)
        self.assertFalse(result['complete'])
        self.assertEqual(result['status'], 'source_changed')


if __name__ == '__main__':
    unittest.main()
