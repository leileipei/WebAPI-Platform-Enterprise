"""Synthetic callbacks and private temporary files only; no engine access."""
import copy
import hashlib
import json
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from local_diagnostics import t0, t0_diagnostics


class StageContracts(unittest.TestCase):
    def setUp(self):
        self.t0 = t0
        self.temp = tempfile.TemporaryDirectory(prefix='cache-stage-synthetic-')
        self.addCleanup(self.temp.cleanup)
        self.q = Path(self.temp.name)
        self.ids = {'containers': ['a' * 64], 'volumes': ['synthetic-volume'], 'networks': ['b' * 64]}
        raw = {'containers': [{'Id': 'a' * 64, 'Name': '/synthetic', 'Created': 'old', 'Image': 'sha256:fake', 'Config': {'Cmd': ['SYNTHETIC_COMMAND'], 'Env': ['RAW_ENV_SENTINEL'], 'Labels': {}}, 'HostConfig': {'AutoRemove': False, 'NetworkMode': 'none'}, 'Mounts': []}],
               'volumes': [{'Name': 'synthetic-volume', 'CreatedAt': 'old', 'Driver': 'local', 'Scope': 'local'}],
               'networks': [{'Id': 'b' * 64, 'Name': 'synthetic-network', 'Created': 'old', 'Driver': 'bridge', 'Internal': False, 'IPAM': {'Config': []}}]}
        self.meta = {k: self.t0.safe_metadata(k, rows) for k, rows in raw.items()}
        self.diag_module = None

    def diagnostics(self, stream=None):
        self.diag_module = t0_diagnostics
        return self.diag_module.T0Diagnostics(self.q / 'diagnostics', stream_path=stream)

    def flow(self, module, events=None, inventories=None, metadata=None, diagnostics=None, state=None, healthy=None):
        calls = []
        inventories = iter(inventories or [self.ids] * 3)
        metadata = iter(metadata or [self.meta] * 2)
        def inventory():
            calls.append('inventory')
            return copy.deepcopy(next(inventories))
        def read_meta(_):
            calls.append('metadata')
            value = next(metadata)
            if isinstance(value, BaseException):
                raise value
            return copy.deepcopy(value)
        def event_window(_, __):
            calls.append('events')
            return copy.deepcopy(events if events is not None else {'completed': True, 'retentionSaturation': False, 'rows': []})
        state = {} if state is None else state
        kwargs = {} if diagnostics is None else {'diagnostics': diagnostics}
        try:
            value = module.capture_once(state, inventory, read_meta, event_window, healthy or (lambda: None), **kwargs)
            outcome = ('sealed', value[2]['sealed'])
        except BaseException as error:
            outcome = (type(error).__name__, error.args[0] if isinstance(error, RuntimeError) else None)
        return outcome, calls, state

    def manifest(self):
        return json.loads((self.q / 'diagnostics/manifest.json').read_text())

    def test_event_failure_retains_all_five_snapshots_unsealed(self):
        diag = self.diagnostics()
        events = {'completed': True, 'retentionSaturation': False, 'rows': [{'type': 'container', 'action': 'exec_create', 'id': 'c' * 64, 'timeNano': 1}]}
        outcome, calls, state = self.flow(self.t0, events=events, diagnostics=diag)
        self.assertEqual(outcome, ('RuntimeError', 'T0_RESOURCE_EVENT_DURING_BRACKET'))
        m = self.manifest()
        self.assertFalse(m['sealed'])
        self.assertEqual(m['failureStage'], 'event-gate')
        self.assertEqual(m['error']['code'], 'T0_RESOURCE_EVENT_DURING_BRACKET')
        self.assertTrue({'inventory-A', 'metadata-A', 'inventory-B', 'metadata-B', 'inventory-C', 'events'} <= {r['stage'] for r in m['records']})
        self.assertEqual(calls, ['inventory', 'metadata', 'inventory', 'metadata', 'inventory', 'events'])
        self.assertEqual(state['safeErrorCode'], 'T0_RESOURCE_EVENT_DURING_BRACKET')

    def test_inspect_failure_retains_prior_snapshots_and_safe_code(self):
        diag = self.diagnostics()
        error = subprocess.TimeoutExpired(['RAW_ARGV_SENTINEL'], 1)
        outcome, _, state = self.flow(self.t0, metadata=[self.meta, error], diagnostics=diag)
        self.assertEqual(outcome[0], 'TimeoutExpired')
        m = self.manifest()
        self.assertEqual(m['failureStage'], 'metadata-B')
        self.assertEqual(m['error'], {'type': 'TimeoutExpired', 'code': 'T0_STAGE_CALLBACK_FAILED'})
        self.assertEqual({r['stage'] for r in m['records']}, {'start', 'inventory-A', 'metadata-A', 'inventory-B'})
        self.assertNotIn('RAW_ARGV_SENTINEL', ''.join(f.read_text() for f in (self.q / 'diagnostics').glob('*.json')))

    def test_inventory_change_retains_failed_B_without_claiming_seal(self):
        diag = self.diagnostics()
        changed = copy.deepcopy(self.ids)
        changed['containers'] = []
        outcome, _, _ = self.flow(self.t0, inventories=[self.ids, changed, changed], diagnostics=diag)
        self.assertEqual(outcome, ('RuntimeError', 'T0_INVENTORY_CHANGED'))
        m = self.manifest()
        self.assertFalse(m['sealed'])
        records = {r['stage']: r for r in m['records']}
        self.assertIn('inventory-B', records)
        data = json.loads((self.q / 'diagnostics' / records['inventory-B']['file']).read_text())
        self.assertEqual(data['payload']['containers'], [])

    def test_same_name_volume_recreation_keeps_both_metadata_snapshots(self):
        diag = self.diagnostics()
        changed = copy.deepcopy(self.meta)
        changed['volumes']['synthetic-volume']['CreatedAt'] = 'new'
        outcome, _, _ = self.flow(self.t0, metadata=[self.meta, changed], diagnostics=diag)
        self.assertEqual(outcome, ('RuntimeError', 'T0_METADATA_CHANGED'))
        self.assertTrue({'metadata-A', 'metadata-B'} <= {r['stage'] for r in self.manifest()['records']})

    def test_stage_files_are_private_and_hash_bound(self):
        diag = self.diagnostics()
        outcome, _, _ = self.flow(self.t0, diagnostics=diag)
        self.assertEqual(outcome, ('sealed', True))
        m = self.manifest()
        self.assertEqual(m['outcome'], 'capture-complete')
        self.assertFalse(m['sealed'])  # Only the orchestrator can durably seal T0.
        self.assertEqual(stat.S_IMODE((self.q / 'diagnostics').stat().st_mode), 0o700)
        for row in m['records']:
            f = self.q / 'diagnostics' / row['file']
            self.assertEqual(hashlib.sha256(f.read_bytes()).hexdigest(), row['sha256'])
            self.assertEqual(stat.S_IMODE(f.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE((self.q / 'diagnostics/manifest.json').stat().st_mode), 0o600)

    def test_consumed_state_never_rewrites_diagnostics(self):
        diag = self.diagnostics()
        _, _, state = self.flow(self.t0, diagnostics=diag)
        before = (self.q / 'diagnostics/manifest.json').read_bytes()
        outcome, calls, _ = self.flow(self.t0, diagnostics=diag, state=state)
        self.assertEqual(outcome, ('RuntimeError', 'T0_ALREADY_CONSUMED_NO_RECAPTURE'))
        self.assertEqual(calls, [])
        self.assertEqual((self.q / 'diagnostics/manifest.json').read_bytes(), before)

    def test_classification_never_allows_exec_or_unknown_resource_actions(self):
        for action in ['create', 'destroy', 'rename', 'update', 'exec_create', 'exec_start', 'exec_die', 'future-unknown']:
            with self.subTest(action=action):
                # Each subtest gets a separate, owned temporary diagnostic path.
                if (self.q / 'diagnostics').exists():
                    self.q = self.q.parent / (self.q.name + '-' + action)
                    self.q.mkdir(mode=0o700)
                    self.addCleanup(lambda path=self.q: __import__('shutil').rmtree(path))
                diag = self.diagnostics()
                event = {'completed': True, 'retentionSaturation': False, 'rows': [{'type': 'container', 'action': action, 'id': 'c' * 64, 'timeNano': 1}]}
                new, _, _ = self.flow(self.t0, events=event, diagnostics=diag)
                self.assertEqual(new, ('RuntimeError', 'T0_RESOURCE_EVENT_DURING_BRACKET'))
                summary = self.manifest()['eventSummary']
                self.assertTrue(summary['classificationOnly'])
                self.assertFalse(summary['grantsAdmission'])
                category = 'execution-activity' if action.startswith('exec_') else ('unknown' if action == 'future-unknown' else 'identity-related-signal')
                self.assertEqual(summary['categories'][category], 1)

    def test_failed_callback_is_not_retried(self):
        diag = self.diagnostics()
        outcome, calls, _ = self.flow(self.t0, metadata=[OSError('synthetic inspect failure'), self.meta], diagnostics=diag)
        self.assertEqual(outcome[0], 'OSError')
        self.assertEqual(calls, ['inventory', 'metadata'])
        self.assertFalse(self.manifest()['sealed'])

    def test_synthetic_events_match_stream_but_still_reject(self):
        rows = [{'type': 'container', 'action': action, 'id': identity, 'timeNano': index * 10 + offset}
                for index, identity in enumerate(['c' * 64, 'd' * 64])
                for offset, action in enumerate(['exec_create', 'exec_start', 'exec_die'])]
        stream = self.q / 'synthetic-events.jsonl'
        stream.write_text(''.join(json.dumps(row) + '\n' for row in rows))
        diag = self.diagnostics(stream=stream)
        outcome, _, _ = self.flow(self.t0, events={'completed': True, 'retentionSaturation': False, 'rows': rows}, diagnostics=diag)
        self.assertEqual(outcome, ('RuntimeError', 'T0_RESOURCE_EVENT_DURING_BRACKET'))
        summary = self.manifest()['eventSummary']
        self.assertEqual(summary['categories']['execution-activity'], 6)
        self.assertEqual(summary['distinctActors'], 2)
        self.assertEqual(summary['streamComparison']['matchedWindowRows'], 6)
        self.assertEqual(summary['streamComparison']['unmatchedWindowRows'], 0)
        self.assertEqual(summary['owner'], 'Unknown')
        self.assertEqual(summary['healthCheckOrigin'], 'Unknown')

    def test_protected_metadata_subset_reports_foreign_additions(self):
        current = copy.deepcopy(self.ids)
        current['containers'].append('c' * 64)
        result = self.t0.verify_protected(self.ids, self.meta, current, copy.deepcopy(self.meta))
        self.assertEqual(result['foreignAdditions']['containers'], ['c' * 64])
        self.assertFalse(any(result['missingStartingResources'].values()))

    def test_full_current_metadata_requires_explicit_protected_projection(self):
        current = copy.deepcopy(self.ids)
        current['containers'].append('c' * 64)
        full = copy.deepcopy(self.meta)
        full['containers']['c' * 64] = {'Name': '/synthetic-foreign'}
        with self.assertRaisesRegex(RuntimeError, 'INCOMPLETE_METADATA_IDS'):
            self.t0.verify_protected(self.ids, self.meta, current, full)
        protected = {kind: {identity: full[kind][identity] for identity in self.ids[kind]} for kind in self.ids}
        result = self.t0.verify_protected(self.ids, self.meta, current, protected)
        self.assertEqual(result['foreignAdditions']['containers'], ['c' * 64])

    def test_projection_drops_raw_env_argv_and_action_suffix(self):
        diag = self.diagnostics()
        polluted = copy.deepcopy(self.meta)
        polluted['containers']['a' * 64].update(Env=['RAW_ENV_SENTINEL'], Cmd=['RAW_COMMAND_SENTINEL'])
        events = {'completed': True, 'retentionSaturation': False, 'rows': [{'type': 'container', 'action': 'exec_create: RAW_ARGV_SENTINEL', 'id': 'c' * 64, 'timeNano': 1, 'argv': 'RAW_ARGV_SENTINEL'}]}
        self.flow(self.t0, metadata=[polluted, polluted], events=events, diagnostics=diag)
        text = ''.join(f.read_text() for f in (self.q / 'diagnostics').glob('*.json'))
        for sentinel in ['RAW_ENV_SENTINEL', 'RAW_COMMAND_SENTINEL', 'RAW_ARGV_SENTINEL']:
            self.assertNotIn(sentinel, text)

    def test_manifest_write_error_does_not_mask_original_gate_failure(self):
        diag = self.diagnostics()
        real_write = self.diag_module.write_private_json_exclusive
        def fail_manifest(path, value):
            if Path(path).name == 'manifest.json':
                raise OSError('RAW_WRITE_ERROR_SENTINEL')
            return real_write(path, value)
        event = {'completed': True, 'retentionSaturation': False, 'rows': [{'type': 'container', 'action': 'exec_die', 'id': 'c' * 64, 'timeNano': 1}]}
        with patch.object(self.diag_module, 'write_private_json_exclusive', side_effect=fail_manifest):
            outcome, _, state = self.flow(self.t0, events=event, diagnostics=diag)
        self.assertEqual(outcome, ('RuntimeError', 'T0_RESOURCE_EVENT_DURING_BRACKET'))
        self.assertEqual(state['diagnosticFailureType'], 'OSError')
        self.assertFalse((self.q / 'diagnostics/manifest.json').exists())

    def test_partial_stage_write_is_marked_unconfirmed_not_dropped(self):
        diag = self.diagnostics()
        real_write = self.diag_module.write_private_json_exclusive
        def fail_stage(path, value):
            if str(path).endswith('-inventory-B.json'):
                Path(path).write_text('{"incomplete":')
                Path(path).chmod(0o600)
                raise OSError('RAW_WRITE_ERROR_SENTINEL')
            return real_write(path, value)
        with patch.object(self.diag_module, 'write_private_json_exclusive', side_effect=fail_stage):
            outcome, _, _ = self.flow(self.t0, diagnostics=diag)
        self.assertEqual(outcome[0], 'OSError')
        m = self.manifest()
        self.assertFalse(m['sealed'])
        self.assertEqual(m['unconfirmedStageFile']['stage'], 'inventory-B')
        self.assertEqual(m['unconfirmedStageFile']['state'], 'write-not-confirmed')
        self.assertNotIn('RAW_WRITE_ERROR_SENTINEL', json.dumps(m))

    def test_stream_comparison_does_not_swallow_deadline(self):
        diag = self.diagnostics(stream=self.q / 'unused-stream')
        def expired():
            raise TimeoutError('SAFE_SYNTHETIC_DEADLINE')
        diag.budget = expired
        with self.assertRaises(TimeoutError):
            diag.compare_stream([])

    def test_observer_failure_records_last_completed_stage(self):
        diag = self.diagnostics()
        checks = []
        def healthy():
            checks.append(1)
            if len(checks) == 3:
                raise RuntimeError('OBSERVER_NOT_HEALTHY')
        outcome, _, _ = self.flow(self.t0, diagnostics=diag, healthy=healthy)
        self.assertEqual(outcome, ('RuntimeError', 'OBSERVER_NOT_HEALTHY'))
        m = self.manifest()
        self.assertEqual(m['failureStage'], 'health-before-B')
        self.assertEqual({r['stage'] for r in m['records']}, {'start', 'inventory-A', 'metadata-A'})

    def test_incomplete_event_window_is_retained_and_rejected(self):
        diag = self.diagnostics()
        events = {'completed': False, 'retentionSaturation': False, 'rows': []}
        outcome, _, _ = self.flow(self.t0, events=events, diagnostics=diag)
        self.assertEqual(outcome, ('RuntimeError', 'T0_EVENT_COVERAGE_UNKNOWN'))
        m = self.manifest()
        self.assertEqual(m['failureStage'], 'event-gate')
        self.assertIn('events', {r['stage'] for r in m['records']})
        self.assertFalse(m['sealed'])


if __name__ == '__main__':
    with patch('subprocess.run', side_effect=AssertionError('NO_EXTERNAL_PROCESS_ALLOWED')), patch('subprocess.Popen', side_effect=AssertionError('NO_EXTERNAL_PROCESS_ALLOWED')):
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(StageContracts))
    raise SystemExit(0 if result.wasSuccessful() else 1)
