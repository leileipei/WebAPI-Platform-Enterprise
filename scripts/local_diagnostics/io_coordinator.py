"""Pure event model. Intents are data, never IO, timers or runtime authority."""
from collections import OrderedDict
from copy import deepcopy
from dataclasses import dataclass, fields
import json
import re

MAX_INT = (1 << 63) - 1
DOMAINS = ('raw_logs', 'trx_copy')
CHANNELS = ('stdout', 'stderr')


def _uint(value, minimum=0):
    return type(value) is int and minimum <= value <= MAX_INT


def _token(value):
    return type(value) is str and re.fullmatch(r'[A-Za-z0-9_-]{1,64}', value) is not None


@dataclass(frozen=True)
class IoLimits:
    global_deadline_ns: int
    work_deadline_ns: int
    io_completion_deadline_ns: int
    drain_deadline_ns: int
    finalize_deadline_ns: int
    drain_window_ns: int
    capture_limit_bytes: int
    trx_limit_bytes: int
    aggregate_limit_bytes: int
    max_chunk_bytes: int
    max_queued_bytes: int
    max_discard_observed_bytes: int
    max_drain_events: int
    max_control_frame_bytes: int

    def __post_init__(self):
        if (not all(_uint(getattr(self, f.name), 1) for f in fields(self))
                or not self.work_deadline_ns <= self.drain_deadline_ns <= self.finalize_deadline_ns <= self.global_deadline_ns
                or self.io_completion_deadline_ns > self.finalize_deadline_ns
                or self.max_chunk_bytes > 65536 or self.max_queued_bytes > 65536
                or not 128 <= self.max_control_frame_bytes <= 4096
                or self.max_drain_events > 256):
            raise ValueError('INVALID_IO_LIMITS')


class IoDeadlineCoordinator:
    """Single-owner deterministic state. No callbacks or worker-held locks.

    Constructor starts a model, not a runtime. Only a bounded window of terminal
    receipts is retained; an older replay fails closed rather than allocating an
    unbounded deduplication ledger. File references and leases are correlation
    tokens, not permissions to access or close anything.
    """
    def __init__(self, limits, attempt_epoch, start_ns, *, worker_epoch, leases):
        if (type(limits) is not IoLimits or not _token(attempt_epoch) or not _token(worker_epoch)
                or not _uint(start_ns) or start_ns >= min(limits.work_deadline_ns, limits.io_completion_deadline_ns)
                or type(leases) is not dict or len(leases) != 2
                or not all(type(k) is str for k in leases) or set(leases) != set(CHANNELS)
                or not all(_token(v) for v in leases.values())):
            raise ValueError('INVALID_MODEL_CONFIGURATION')
        self.limits = limits
        self._attempt, self._worker = attempt_epoch, worker_epoch
        self._leases = dict(leases)
        self._last = start_ns
        self._state = 'CAPTURING'
        self._primary, self._secondary = None, []
        self._admitted = dict.fromkeys(DOMAINS, 0)
        self._reserved = dict.fromkeys(DOMAINS, 0)
        self._durable = dict.fromkeys(DOMAINS, 0)
        self._eof = dict.fromkeys(CHANNELS, False)
        self._read_state = dict.fromkeys(CHANNELS, 'open')
        self._pipe_sequences = dict.fromkeys(CHANNELS, 0)
        self._pending = self._queued = None
        self._sequence = 0
        self._history = OrderedDict()
        self._discarded = self._drain_events = 0
        self._drain_at = None
        self._worker_state = 'unknown'
        self._worker_blocked = False
        self._rejected_event_bytes = 0
        self._finish_sent = False
        self._poll_turn = 0
        self._returned = self._returned_at = None
        self._intents = []

    def _fail(self, reason, now):
        if self._primary is None:
            self._primary = reason
        elif reason != self._primary and reason not in self._secondary:
            self._secondary.append(reason)
        if self._drain_at is None:
            self._drain_at = min(now + self.limits.drain_window_ns,
                                 self.limits.drain_deadline_ns, self.limits.finalize_deadline_ns,
                                 self.limits.global_deadline_ns)
            self._state = 'DRAINING'

    def _return(self, now, complete=False):
        self._returned_at = now
        self._state = 'RETURNED_OBSERVED' if complete and self._primary is None else 'RETURNED_INCOMPLETE'
        self._returned = self._summary()
        self._intents = [{'kind': 'return_summary'}]

    def _summary(self):
        return {'state': self._state, 'primary_failure': self._primary,
                'secondary_failures': list(self._secondary), 'returned_at_ns': self._returned_at,
                'deadline_decision_observed': any('deadline' in s for s in [self._primary or ''] + self._secondary),
                'worker_state': self._worker_state, 'io_stopped_proven': False,
                'channel_eof': dict(self._eof), 'read_end_state': dict(self._read_state),
                'admitted_bytes': dict(self._admitted), 'reserved_bytes': dict(self._reserved),
                'durable_bytes': dict(self._durable), 'discarded_bytes_observed': self._discarded,
                'rejected_event_bytes': self._rejected_event_bytes,
                'unobserved_tail_bytes': 0 if all(self._eof.values()) else None,
                'queued_bytes': len(self._queued.get('payload', b'')) if self._queued else 0,
                'evidence_complete': self._state == 'RETURNED_OBSERVED',
                'grants_admission': False, 'summary_durable': False}

    def snapshot(self):
        return deepcopy(self._returned if self._returned is not None else self._summary())

    def _decision(self):
        return {'summary': self.snapshot(), 'intents': deepcopy(self._intents)}

    def _valid_event(self, event, now):
        if type(event) is not dict or len(event) > 16 or not all(type(k) is str for k in event):
            return False
        kind = event.get('kind')
        schemas = {
            'clock_tick': set(), 'stop': {'reason'},
            'copy_request': {'max_admission_bytes', 'owned_input_ref', 'owned_output_ref'},
            'pipe_data': {'channel', 'lease_generation', 'event_sequence', 'data'},
            'pipe_eof': {'channel', 'lease_generation', 'event_sequence'},
            'pipe_would_block': {'channel', 'lease_generation', 'event_sequence'},
            'pipe_error': {'channel', 'lease_generation', 'event_sequence'},
            'worker_state': {'worker_epoch', 'state'},
            'local_close_ack': {'channel', 'lease_generation'},
            'write_ack': {'worker_epoch', 'job_sequence', 'budget_domain', 'admitted_bytes',
                          'observed_discarded_bytes', 'cumulative_admitted_bytes',
                          'durability', 'evidence_complete', 'terminal'},
            'finish_ack': {'worker_epoch', 'job_sequence', 'evidence_complete', 'durability'},
        }
        if (type(kind) is not str or len(kind) > 32 or kind not in schemas
                or type(event.get('attempt_epoch')) is not str
                or len(event['attempt_epoch']) > 64
                or event['attempt_epoch'] != self._attempt):
            return False
        required = schemas[kind] | {'kind', 'attempt_epoch'}
        optional = {'completed_at_ns'} if kind in ('write_ack', 'finish_ack') else set()
        if not required <= event.keys() or not event.keys() <= required | optional:
            return False
        header = {k: v for k, v in event.items() if k != 'data'}
        if not all(type(v) in (str, int, bool) for v in header.values()):
            return False
        if any(type(v) is str and len(v) > 64 for v in header.values()):
            return False
        if any(type(v) is int and not _uint(v) for v in header.values()):
            return False
        if len(json.dumps(header, separators=(',', ':')).encode('utf-8')) > self.limits.max_control_frame_bytes:
            return False
        if 'worker_epoch' in event and event['worker_epoch'] != self._worker:
            return False
        if 'job_sequence' in event and not _uint(event['job_sequence'], 1):
            return False
        if 'completed_at_ns' in event and (not _uint(event['completed_at_ns']) or event['completed_at_ns'] > now):
            return False
        if 'channel' in event and event['channel'] not in CHANNELS:
            return False
        if 'lease_generation' in event and event['lease_generation'] != self._leases[event['channel']]:
            return False
        if 'event_sequence' in event and not _uint(event['event_sequence'], 1):
            return False
        if kind == 'pipe_data' and (type(event['data']) is not bytes or not 1 <= len(event['data']) <= self.limits.max_chunk_bytes):
            return False
        if kind == 'stop' and event['reason'] != 'business_failure':
            return False
        if kind == 'copy_request' and (not _uint(event['max_admission_bytes'], 1)
                or not _token(event['owned_input_ref']) or not _token(event['owned_output_ref'])):
            return False
        if kind == 'worker_state' and event['state'] not in ('pending', 'finished', 'unknown'):
            return False
        if kind in ('write_ack', 'finish_ack') and (type(event['evidence_complete']) is not bool
                or event['durability'] not in ('confirmed', 'unconfirmed')):
            return False
        if kind == 'write_ack' and (event['budget_domain'] not in DOMAINS
                or type(event['terminal']) is not bool
                or not all(_uint(event[k]) for k in ('admitted_bytes', 'observed_discarded_bytes', 'cumulative_admitted_bytes'))):
            return False
        return True

    def _available(self, domain):
        cap = self.limits.capture_limit_bytes if domain == 'raw_logs' else self.limits.trx_limit_bytes
        return min(cap - self._admitted[domain] - self._reserved[domain],
                   self.limits.aggregate_limit_bytes - sum(self._admitted.values()) - sum(self._reserved.values()))

    def _job(self, operation, domain, maximum, **values):
        self._sequence += 1
        if self._sequence > MAX_INT:
            raise ValueError('MODEL_SEQUENCE_EXHAUSTED')
        return dict(operation=operation, budget_domain=domain, max_admission_bytes=maximum,
                    job_sequence=self._sequence, confirmed=0, discarded=0, durable=0,
                    absolute_completion_deadline_ns=min(self.limits.io_completion_deadline_ns,
                                                         self.limits.finalize_deadline_ns,
                                                         self.limits.global_deadline_ns), **values)

    def _dispatch(self, job):
        self._pending = job
        self._worker_state = 'pending'
        intent = {k: v for k, v in job.items() if k not in ('confirmed', 'discarded', 'durable')}
        intent.update(kind='submit_finish' if job['operation'] == 'finish' else 'submit_write',
                      attempt_epoch=self._attempt, worker_epoch=self._worker)
        self._intents.append(intent)

    def _offer(self, job, now):
        if self._pending is not None or self._worker_blocked:
            if self._queued is not None or len(job.get('payload', b'')) > self.limits.max_queued_bytes:
                self._fail('queue_full', now)
                return False
            self._queued = job
            self._fail('queue_full', now)
        else:
            self._dispatch(job)
        self._reserved[job['budget_domain']] += job['max_admission_bytes']
        return True

    def _omit(self, count, now):
        if count > self.limits.max_discard_observed_bytes - self._discarded:
            # The event exceeded the read grant. Preserve its size as rejected
            # input, not as an accepted observation in the bounded drain ledger.
            self._rejected_event_bytes += count
            self._fail('drain_byte_limit', now)
            self._return(now)
            return False
        self._discarded += count
        return True

    def _pipe(self, event, now):
        channel = event['channel']
        if self._eof[channel] or event['event_sequence'] != self._pipe_sequences[channel] + 1:
            self._fail('invalid_event', now)
            return
        self._pipe_sequences[channel] += 1
        kind = event['kind']
        if kind == 'pipe_eof':
            self._eof[channel] = True
        elif kind == 'pipe_error':
            self._fail('pipe_error', now)
        elif kind == 'pipe_data':
            data = event['data']
            accepted = min(len(data), max(0, self._available('raw_logs')))
            omitted = len(data) - accepted
            cannot_queue = ((self._pending is not None or self._worker_blocked)
                            and (self._queued is not None or accepted > self.limits.max_queued_bytes))
            if accepted and cannot_queue:
                self._fail('queue_full', now)
                self._omit(len(data), now)
                return
            if omitted > self.limits.max_discard_observed_bytes - self._discarded:
                self._omit(len(data), now)
                return
            if accepted:
                self._offer(self._job('log_chunk', 'raw_logs', accepted, channel=channel, payload=data[:accepted]), now)
            self._omit(omitted, now)
            if omitted or self._available('raw_logs') == 0:
                self._fail('log_ceiling', now)

    def _write_ack(self, event, now):
        seq = event['job_sequence']
        fingerprint = tuple(sorted(event.items()))
        if seq in self._history:
            if self._history[seq] != fingerprint:
                self._fail('invalid_receipt', now)
            return
        job = self._pending
        if job is None or job['operation'] == 'finish' or job['job_sequence'] != seq or job['budget_domain'] != event['budget_domain']:
            self._fail('invalid_receipt', now)
            return
        domain = job['budget_domain']
        admitted, discarded = event['admitted_bytes'], event['observed_discarded_bytes']
        delta = admitted - job['confirmed']
        if (delta < 0 or discarded < job['discarded'] or admitted + discarded > job['max_admission_bytes']
                or event['cumulative_admitted_bytes'] != self._admitted[domain] + delta
                or (event['terminal'] and job['operation'] == 'log_chunk' and admitted + discarded != job['max_admission_bytes'])):
            self._fail('invalid_receipt', now)
            return
        if discarded - job['discarded'] > self.limits.max_discard_observed_bytes - self._discarded:
            self._rejected_event_bytes += discarded - job['discarded']
            self._fail('invalid_receipt', now)
            self._return(now)
            return
        self._admitted[domain] += delta
        self._reserved[domain] -= delta
        self._discarded += discarded - job['discarded']
        job['confirmed'], job['discarded'] = admitted, discarded
        if event['durability'] == 'confirmed':
            self._durable[domain] += admitted - job['durable']
            job['durable'] = admitted
        if event['terminal']:
            self._reserved[domain] -= job['max_admission_bytes'] - admitted
            self._history[seq] = fingerprint
            if len(self._history) > 16:
                self._history.popitem(last=False)
            self._pending = None
            self._worker_state = 'unknown'
            if discarded or not event['evidence_complete']:
                self._fail('writer_evidence_incomplete', now)

    def _handle(self, event, now):
        kind = event['kind']
        if kind.startswith('pipe_'):
            self._pipe(event, now)
        elif kind == 'stop':
            self._fail('business_failure', now)
        elif kind == 'copy_request':
            count = event['max_admission_bytes']
            if self._primary is not None or count > self._available('trx_copy'):
                self._fail('copy_limit', now)
            else:
                self._offer(self._job('trx_copy', 'trx_copy', count,
                                      owned_input_ref=event['owned_input_ref'], owned_output_ref=event['owned_output_ref']), now)
        elif kind == 'write_ack':
            self._write_ack(event, now)
        elif kind == 'finish_ack':
            job = self._pending
            if job is None or job['operation'] != 'finish' or event['job_sequence'] != job['job_sequence']:
                self._fail('invalid_receipt', now)
            else:
                self._pending = None
                if event['durability'] == 'confirmed' and event['evidence_complete']:
                    self._durable['raw_logs'] = self._admitted['raw_logs']
                complete = (event['durability'] == 'confirmed' and event['evidence_complete']
                            and all(self._eof.values()) and not self._discarded and not any(self._reserved.values())
                            and self._durable == self._admitted and self._primary is None)
                if not complete:
                    self._fail('evidence_incomplete', now)
                self._return(now, complete)
        elif kind == 'worker_state':
            self._worker_state = event['state']
            if event['state'] != 'pending' or self._pending is None:
                self._worker_blocked = True
                self._fail('worker_unavailable', now)
        elif kind == 'local_close_ack':
            self._read_state[event['channel']] = 'unknown'
            self._fail('unproved_close', now)

    def _advance(self, now):
        if self._returned is not None:
            return
        if self._drain_at is not None and now >= self._drain_at:
            self._fail('drain_deadline', now)
            self._return(now)
            return
        if self._discarded >= self.limits.max_discard_observed_bytes:
            self._fail('drain_byte_limit', now)
            self._return(now)
            return
        if not self._worker_blocked and self._pending is None and self._queued is not None:
            job, self._queued = self._queued, None
            self._dispatch(job)
        if (not self._worker_blocked and all(self._eof.values()) and self._pending is None
                and self._queued is None and not self._finish_sent):
            self._finish_sent = True
            self._state = 'FINALIZING'
            self._dispatch(self._job('finish', None, 0))
        if self._state != 'FINALIZING' and not all(self._eof.values()):
            order = [channel for channel in CHANNELS[self._poll_turn:] + CHANNELS[:self._poll_turn]
                     if not self._eof[channel]]
            self._poll_turn = 1 - self._poll_turn
            allowance = self.limits.max_chunk_bytes
            if self._available('raw_logs') <= 0:
                allowance = min(allowance, self.limits.max_discard_observed_bytes - self._discarded)
            elif self._pending is not None or self._worker_blocked:
                allowance = 0 if self._queued is not None else min(allowance, self.limits.max_queued_bytes)
            self._intents.append({'kind': 'poll_request', 'channels': order,
                                  'max_read_bytes': allowance,
                                  'poll_deadline_ns': min(now + 25000000, self.limits.io_completion_deadline_ns,
                                                          self.limits.finalize_deadline_ns,
                                                          self._drain_at or self.limits.work_deadline_ns)})

    def step(self, now_ns, event):
        self._intents = []
        if self._returned is not None:
            return self._decision()
        if not _uint(now_ns) or now_ns < self._last or now_ns > MAX_INT - self.limits.drain_window_ns:
            self._fail('invalid_clock', self._last)
            self._return(self._last)
            return self._decision()
        self._last = now_ns
        for deadline, reason in ((self.limits.global_deadline_ns, 'global_deadline'),
                                 (self.limits.finalize_deadline_ns, 'finalize_deadline'),
                                 (self.limits.io_completion_deadline_ns, 'io_deadline')):
            if now_ns >= deadline:
                self._fail(reason, now_ns)
                self._return(now_ns)
                return self._decision()
        if self._drain_at is not None and now_ns >= self._drain_at:
            self._fail('drain_deadline', now_ns)
            self._return(now_ns)
            return self._decision()
        draining_before_event = self._drain_at is not None
        if now_ns >= self.limits.work_deadline_ns:
            self._fail('work_deadline', now_ns)
            if now_ns >= self._drain_at:
                self._fail('drain_deadline', now_ns)
                self._return(now_ns)
                return self._decision()
        if not self._valid_event(event, now_ns):
            self._fail('invalid_event', now_ns)
        else:
            self._handle(event, now_ns)
        if self._returned is None and draining_before_event:
            self._drain_events += 1
            if self._drain_events >= self.limits.max_drain_events:
                self._fail('drain_event_limit', now_ns)
                self._return(now_ns)
        self._advance(now_ns)
        return self._decision()
