"""Fake clock and literal events only; no real producer, pipe or worker."""
import importlib.util
from pathlib import Path
import sys
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
if importlib.util.find_spec('local_diagnostics.io_coordinator'):
    from local_diagnostics import io_coordinator as model
else:
    model = None


def scenario(identity):
    def attach(function):
        function.scenario_id = identity
        return function
    return attach


class FakeClock:
    def at(self, value):
        return value


class CoordinatorContracts(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(model, 'pure IO coordinator is not implemented')
        self.clock = FakeClock()

    def coordinator(self, **changes):
        values = dict(global_deadline_ns=100, work_deadline_ns=70,
                      io_completion_deadline_ns=80, drain_deadline_ns=90,
                      finalize_deadline_ns=95, drain_window_ns=5,
                      capture_limit_bytes=8, trx_limit_bytes=6, aggregate_limit_bytes=12,
                      max_chunk_bytes=4, max_queued_bytes=4,
                      max_discard_observed_bytes=4, max_drain_events=8,
                      max_control_frame_bytes=4096)
        values.update(changes)
        return model.IoDeadlineCoordinator(model.IoLimits(**values), 'attempt', 0,
                                            worker_epoch='worker',
                                            leases={'stdout': 'out', 'stderr': 'err'})

    def event(self, kind, **values):
        return dict(kind=kind, attempt_epoch='attempt', **values)

    def step(self, coordinator, now, kind, **values):
        return coordinator.step(self.clock.at(now), self.event(kind, **values))

    def pipe(self, coordinator, now, kind='pipe_data', channel='stdout', seq=1, data=b'abcd'):
        values = dict(channel=channel, lease_generation='out' if channel == 'stdout' else 'err', event_sequence=seq)
        if kind == 'pipe_data':
            values['data'] = data
        return self.step(coordinator, now, kind, **values)

    def ack(self, coordinator, now, seq=1, admitted=4, cumulative=4,
            discarded=0, domain='raw_logs', terminal=True, complete=True):
        return self.step(coordinator, now, 'write_ack', worker_epoch='worker',
                         job_sequence=seq, budget_domain=domain, admitted_bytes=admitted,
                         cumulative_admitted_bytes=cumulative, observed_discarded_bytes=discarded,
                         durability='unconfirmed', terminal=terminal, evidence_complete=complete)

    def finish(self, coordinator, now, seq=1):
        return self.step(coordinator, now, 'finish_ack', worker_epoch='worker',
                         job_sequence=seq, evidence_complete=True, durability='confirmed',
                         completed_at_ns=1)

    def test_poll_stops_requesting_channels_after_eof(self):
        for closed, remaining in (('stdout', 'stderr'), ('stderr', 'stdout')):
            with self.subTest(first_eof=closed):
                c = self.coordinator()
                result = self.pipe(c, 1, 'pipe_eof', channel=closed)
                polls = [i for i in result['intents'] if i['kind'] == 'poll_request']
                self.assertEqual([p['channels'] for p in polls], [[remaining]])
                self.pipe(c, 2, channel=remaining)
                result = self.pipe(c, 3, 'pipe_eof', channel=remaining, seq=2)
                self.assertNotIn('poll_request', [i['kind'] for i in result['intents']])
                result = self.ack(c, 4)
                self.assertEqual([i['kind'] for i in result['intents']], ['submit_finish'])
                result = self.finish(c, 5, seq=2)['summary']
                self.assertEqual(result['state'], 'RETURNED_OBSERVED')
                self.assertIsNone(result['primary_failure'])
                self.assertEqual(result['admitted_bytes']['raw_logs'], 4)

    def test_poll_deadline_wakes_for_work_then_drain_deadline(self):
        c = self.coordinator()
        result = self.step(c, 69, 'clock_tick')
        polls = [i for i in result['intents'] if i['kind'] == 'poll_request']
        self.assertEqual([p['poll_deadline_ns'] for p in polls], [70])
        result = self.step(c, 70, 'clock_tick')
        self.assertEqual(result['summary']['primary_failure'], 'work_deadline')
        polls = [i for i in result['intents'] if i['kind'] == 'poll_request']
        self.assertEqual([p['poll_deadline_ns'] for p in polls], [75])
        result = self.step(c, 75, 'clock_tick')
        self.assertEqual(result['summary']['state'], 'RETURNED_INCOMPLETE')
        self.assertEqual([i['kind'] for i in result['intents']], ['return_summary'])

    @scenario('deadline-equality')
    def test_deadline_equality_beats_finish_ack(self):
        c = self.coordinator()
        self.pipe(c, 1, 'pipe_eof')
        self.pipe(c, 2, 'pipe_eof', channel='stderr')
        result = self.finish(c, 80)
        self.assertEqual(result['summary']['state'], 'RETURNED_INCOMPLETE')
        self.assertEqual(result['summary']['primary_failure'], 'io_deadline')

    @scenario('global-equality')
    def test_global_equality_returns_without_io(self):
        c = self.coordinator()
        result = self.step(c, 100, 'clock_tick')
        self.assertEqual(result['summary']['primary_failure'], 'global_deadline')
        self.assertEqual([i['kind'] for i in result['intents']], ['return_summary'])

    @scenario('permanent-writer-block')
    def test_never_acknowledged_writer_keeps_reservation(self):
        c = self.coordinator()
        self.pipe(c, 1)
        result = self.step(c, 80, 'clock_tick')['summary']
        self.assertEqual(result['reserved_bytes']['raw_logs'], 4)
        self.assertIn(result['worker_state'], ['pending', 'unknown'])
        self.assertFalse(result['io_stopped_proven'])

    @scenario('fsync-never-returns')
    def test_no_finish_ack_does_not_claim_durability(self):
        c = self.coordinator()
        self.pipe(c, 1)
        self.ack(c, 2)
        self.pipe(c, 3, 'pipe_eof', seq=2)
        self.pipe(c, 4, 'pipe_eof', channel='stderr')
        result = self.step(c, 80, 'clock_tick')['summary']
        self.assertEqual(result['admitted_bytes']['raw_logs'], 4)
        self.assertEqual(result['durable_bytes']['raw_logs'], 0)
        self.assertFalse(result['evidence_complete'])

    @scenario('worker-lock-held')
    def test_pending_worker_state_cannot_block_pure_decision(self):
        c = self.coordinator()
        self.step(c, 1, 'worker_state', worker_epoch='worker', state='pending')
        result = self.step(c, 80, 'clock_tick')['summary']
        self.assertEqual(result['state'], 'RETURNED_INCOMPLETE')

    @scenario('late-finish')
    def test_returned_outcome_is_immutable_under_late_success(self):
        c = self.coordinator()
        self.pipe(c, 1, 'pipe_eof')
        self.pipe(c, 2, 'pipe_eof', channel='stderr')
        original = self.step(c, 80, 'clock_tick')['summary']
        self.assertEqual(self.finish(c, 81)['summary'], original)

    @scenario('forged-earlier-timestamp')
    def test_receive_deadline_overrides_worker_timestamp(self):
        c = self.coordinator()
        self.pipe(c, 1, 'pipe_eof')
        self.pipe(c, 2, 'pipe_eof', channel='stderr')
        result = self.finish(c, 81)['summary']
        self.assertFalse(result['evidence_complete'])
        self.assertEqual(result['primary_failure'], 'io_deadline')

    @scenario('wrong-generation')
    def test_old_worker_and_lease_do_not_release_or_close(self):
        c = self.coordinator()
        self.pipe(c, 1)
        result = self.step(c, 2, 'worker_state', worker_epoch='old', state='finished')
        self.assertEqual(result['summary']['reserved_bytes']['raw_logs'], 4)
        self.assertNotIn('close_owned_read_end_requested', [i['kind'] for i in result['intents']])
        self.assertIsNotNone(result['summary']['primary_failure'])

    @scenario('duplicate-ack')
    def test_duplicate_terminal_ack_does_not_double_count(self):
        c = self.coordinator()
        self.pipe(c, 1)
        self.ack(c, 2)
        result = self.ack(c, 3)['summary']
        self.assertEqual(result['admitted_bytes']['raw_logs'], 4)
        self.assertEqual(result['reserved_bytes']['raw_logs'], 0)
        self.assertIsNone(result['primary_failure'])

    @scenario('reordered-ack')
    def test_unknown_job_ack_fails_without_releasing_pending_job(self):
        c = self.coordinator()
        self.pipe(c, 1)
        result = self.ack(c, 2, seq=7)['summary']
        self.assertIsNotNone(result['primary_failure'])
        self.assertEqual(result['reserved_bytes']['raw_logs'], 4)

    @scenario('partial-write')
    def test_terminal_partial_write_releases_only_unused_bytes(self):
        c = self.coordinator()
        self.pipe(c, 1)
        result = self.ack(c, 2, admitted=2, cumulative=2, discarded=2, complete=False)['summary']
        self.assertEqual(result['admitted_bytes']['raw_logs'], 2)
        self.assertEqual(result['reserved_bytes']['raw_logs'], 0)
        self.assertEqual(result['durable_bytes']['raw_logs'], 0)
        self.assertFalse(result['evidence_complete'])

    @scenario('uncertain-write')
    def test_timeout_never_resubmits_or_spends_unresolved_bytes(self):
        c = self.coordinator()
        self.pipe(c, 1)
        result = self.step(c, 80, 'clock_tick')
        later = self.pipe(c, 81, seq=2)
        self.assertEqual(later['summary']['reserved_bytes']['raw_logs'], 4)
        self.assertNotIn('submit_write', [i['kind'] for i in result['intents'] + later['intents']])

    @scenario('continuous-producer')
    def test_continuous_output_ends_at_discard_bound(self):
        c = self.coordinator(capture_limit_bytes=2)
        self.pipe(c, 1, data=b'ab')
        self.ack(c, 2, admitted=2, cumulative=2)
        self.pipe(c, 3, seq=2, data=b'cd')
        result = self.pipe(c, 4, seq=3, data=b'ef')['summary']
        self.assertEqual(result['discarded_bytes_observed'], 4)
        self.assertEqual(result['state'], 'RETURNED_INCOMPLETE')
        self.assertIsNone(result['unobserved_tail_bytes'])

    @scenario('would-block-flood')
    def test_empty_poll_events_count_toward_drain_cutoff(self):
        c = self.coordinator(max_drain_events=3)
        self.step(c, 1, 'stop', reason='business_failure')
        for seq in [1, 2, 3]:
            result = self.pipe(c, 1, 'pipe_would_block', seq=seq)
        self.assertEqual(result['summary']['state'], 'RETURNED_INCOMPLETE')
        self.assertFalse(result['summary']['channel_eof']['stdout'])

    @scenario('one-eof')
    def test_single_channel_eof_is_not_overall_eof(self):
        c = self.coordinator()
        result = self.pipe(c, 1, 'pipe_eof')['summary']
        self.assertEqual(result['channel_eof'], {'stdout': True, 'stderr': False})
        self.assertFalse(result['evidence_complete'])

    @scenario('local-close')
    def test_local_close_never_creates_remote_eof(self):
        c = self.coordinator()
        result = self.step(c, 1, 'local_close_ack', channel='stdout', lease_generation='out')['summary']
        self.assertFalse(result['channel_eof']['stdout'])
        self.assertFalse(result['evidence_complete'])

    @scenario('queue-full')
    def test_queue_saturation_keeps_payload_and_reservations_bounded(self):
        c = self.coordinator(capture_limit_bytes=20, aggregate_limit_bytes=24)
        self.pipe(c, 1)
        self.pipe(c, 2, seq=2)
        result = self.pipe(c, 3, channel='stderr')['summary']
        self.assertEqual(result['queued_bytes'], 4)
        self.assertEqual(result['reserved_bytes']['raw_logs'], 8)
        self.assertIsNotNone(result['primary_failure'])

    @scenario('cap-crossing')
    def test_write_offer_never_exceeds_unreserved_allowance(self):
        c = self.coordinator(capture_limit_bytes=5)
        self.pipe(c, 1)
        self.ack(c, 2)
        result = self.pipe(c, 3, seq=2)
        offers = [i for i in result['intents'] if i['kind'] == 'submit_write']
        self.assertEqual(offers[0]['max_admission_bytes'], 1)
        self.assertEqual(offers[0]['payload'], b'a')
        self.assertEqual(result['summary']['discarded_bytes_observed'], 3)

    @scenario('oversize-frame')
    def test_oversize_or_unknown_payload_is_not_echoed(self):
        c = self.coordinator()
        result = self.pipe(c, 1, data=b'SECRET_SENTINEL')
        self.assertIsNotNone(result['summary']['primary_failure'])
        self.assertNotIn('SECRET_SENTINEL', str(result))
        self.assertNotIn('submit_write', [i['kind'] for i in result['intents']])

    @scenario('invalid-clock')
    def test_invalid_clock_and_regression_fail_without_throwing(self):
        for value in [True, -1, 10 ** 400, 0.5]:
            c = self.coordinator()
            self.assertEqual(self.step(c, value, 'clock_tick')['summary']['state'], 'RETURNED_INCOMPLETE')
        c = self.coordinator()
        self.step(c, 2, 'clock_tick')
        self.assertEqual(self.step(c, 1, 'clock_tick')['summary']['primary_failure'], 'invalid_clock')

    @scenario('counter-inconsistency')
    def test_inconsistent_ack_does_not_commit_or_release(self):
        c = self.coordinator()
        self.pipe(c, 1)
        result = self.ack(c, 2, admitted=5, cumulative=5)['summary']
        self.assertEqual(result['admitted_bytes']['raw_logs'], 0)
        self.assertEqual(result['reserved_bytes']['raw_logs'], 4)
        self.assertIsNotNone(result['primary_failure'])

    @scenario('copy-pending')
    def test_pending_trx_keeps_its_own_domain_reserved(self):
        c = self.coordinator()
        self.step(c, 1, 'copy_request', max_admission_bytes=6, owned_input_ref='src', owned_output_ref='dst')
        result = self.step(c, 80, 'clock_tick')['summary']
        self.assertEqual(result['reserved_bytes'], {'raw_logs': 0, 'trx_copy': 6})
        self.assertFalse(result['evidence_complete'])

    @scenario('primary-failure')
    def test_first_business_failure_survives_timeout_and_eof(self):
        c = self.coordinator()
        self.pipe(c, 1)
        self.step(c, 2, 'stop', reason='business_failure')
        first = self.step(c, 80, 'clock_tick')['summary']
        self.assertEqual(first['primary_failure'], 'business_failure')
        self.assertIn('io_deadline', first['secondary_failures'])
        self.assertEqual(self.pipe(c, 81, 'pipe_eof', seq=2)['summary'], first)

    @scenario('fairness')
    def test_poll_orders_rotate_for_two_channels(self):
        c = self.coordinator()
        first = self.step(c, 1, 'clock_tick')
        second = self.step(c, 2, 'clock_tick')
        orders = [[i['channels'] for i in r['intents'] if i['kind'] == 'poll_request'][0]
                  for r in [first, second]]
        self.assertEqual(orders, [['stdout', 'stderr'], ['stderr', 'stdout']])
        self.assertTrue(self.pipe(c, 3, 'pipe_eof', channel='stderr')['summary']['channel_eof']['stderr'])

    @scenario('no-authority')
    def test_tokens_do_not_create_resource_or_descriptor_authority(self):
        c = self.coordinator()
        result = self.step(c, 1, 'stop', reason='business_failure')
        self.assertFalse(result['summary']['grants_admission'])
        self.assertFalse(result['summary']['io_stopped_proven'])
        self.assertNotIn('close_owned_read_end_requested', [i['kind'] for i in result['intents']])

    @scenario('unpersistable-summary')
    def test_returned_summary_is_not_a_durable_manifest(self):
        c = self.coordinator()
        self.pipe(c, 1, 'pipe_eof')
        self.pipe(c, 2, 'pipe_eof', channel='stderr')
        result = self.step(c, 80, 'clock_tick')['summary']
        self.assertFalse(result['summary_durable'])
        self.assertFalse(result['evidence_complete'])

    def test_complete_observed_capture_still_grants_no_runtime_admission(self):
        c = self.coordinator()
        self.pipe(c, 1)
        self.ack(c, 2)
        self.pipe(c, 3, 'pipe_eof', seq=2)
        end = self.pipe(c, 4, 'pipe_eof', channel='stderr')
        self.assertEqual([i['job_sequence'] for i in end['intents'] if i['kind'] == 'submit_finish'], [2])
        result = self.finish(c, 5, seq=2)['summary']
        self.assertEqual(result['state'], 'RETURNED_OBSERVED')
        self.assertTrue(result['evidence_complete'])
        self.assertEqual(result['durable_bytes']['raw_logs'], 4)
        self.assertFalse(result['grants_admission'])
        self.assertFalse(result['summary_durable'])

    def test_identity_validation_never_calls_untrusted_comparison(self):
        class Bomb:
            def __ne__(self, other):
                raise AssertionError('UNTRUSTED_COMPARISON_EXECUTED')
        c = self.coordinator()
        result = c.step(1, {'kind': 'clock_tick', 'attempt_epoch': Bomb()})
        self.assertIsNotNone(result['summary']['primary_failure'])

    def test_newly_established_drain_deadline_wins_current_event(self):
        c = self.coordinator(drain_deadline_ns=72)
        result = self.pipe(c, 72)
        self.assertEqual(result['summary']['state'], 'RETURNED_INCOMPLETE')
        self.assertNotIn('submit_write', [i['kind'] for i in result['intents']])
        self.assertEqual(result['summary']['reserved_bytes']['raw_logs'], 0)

    def test_reported_unavailable_worker_never_receives_finish(self):
        for state in ['pending', 'unknown', 'finished']:
            c = self.coordinator()
            self.step(c, 1, 'worker_state', worker_epoch='worker', state=state)
            self.pipe(c, 2, 'pipe_eof')
            result = self.pipe(c, 3, 'pipe_eof', channel='stderr')
            self.assertNotIn('submit_finish', [i['kind'] for i in result['intents']])

    def test_filled_queue_latches_before_an_extra_chunk(self):
        c = self.coordinator(capture_limit_bytes=20, aggregate_limit_bytes=24)
        self.pipe(c, 1)
        result = self.pipe(c, 2, seq=2)['summary']
        self.assertEqual(result['primary_failure'], 'queue_full')

    def test_rejected_queue_chunk_does_not_overrun_discard_ledger(self):
        c = self.coordinator(capture_limit_bytes=20, aggregate_limit_bytes=24,
                             max_discard_observed_bytes=1)
        self.pipe(c, 1)
        self.pipe(c, 2, seq=2)
        result = self.pipe(c, 3, channel='stderr')['summary']
        self.assertLessEqual(result['discarded_bytes_observed'], 1)
        self.assertEqual(result['rejected_event_bytes'], 4)
        self.assertEqual(result['reserved_bytes']['raw_logs'], 8)

    def test_ack_discard_overflow_is_rejected_before_counter_commit(self):
        c = self.coordinator(capture_limit_bytes=20, aggregate_limit_bytes=24,
                             max_discard_observed_bytes=3)
        self.pipe(c, 1)
        self.ack(c, 2, admitted=2, cumulative=2, discarded=2, complete=False)
        self.pipe(c, 3, seq=2)
        result = self.ack(c, 4, seq=2, admitted=2, cumulative=4, discarded=2, complete=False)['summary']
        self.assertEqual(result['admitted_bytes']['raw_logs'], 2)
        self.assertEqual(result['reserved_bytes']['raw_logs'], 4)
        self.assertEqual(result['discarded_bytes_observed'], 2)

    def test_log_and_copy_reservations_share_aggregate_ceiling(self):
        c = self.coordinator(aggregate_limit_bytes=8)
        self.step(c, 1, 'copy_request', max_admission_bytes=6, owned_input_ref='src', owned_output_ref='dst')
        result = self.pipe(c, 2)['summary']
        self.assertEqual(result['reserved_bytes'], {'raw_logs': 2, 'trx_copy': 6})

    def test_no_poll_read_exceeds_remaining_discard_allowance(self):
        c = self.coordinator(capture_limit_bytes=1)
        self.pipe(c, 1, data=b'a')
        self.ack(c, 2, admitted=1, cumulative=1)
        result = self.pipe(c, 3, seq=2, data=b'bcd')
        polls = [i for i in result['intents'] if i['kind'] == 'poll_request']
        self.assertEqual(polls[0]['max_read_bytes'], 1)


if __name__ == '__main__':
    unittest.main()
