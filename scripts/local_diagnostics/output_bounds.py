"""Offline file bounds and stop decisions. No runtime or lifecycle adapter.

Byte limits constrain only these writers. Blocking filesystem calls, aggregate
quota, producer shutdown and runtime admission are deliberately not provided.
"""
import hashlib
import math
import os
from pathlib import Path
import re
import stat
import threading


REASONS = frozenset({
    'business_failure', 'usage_limit', 'reserve_risk', 'measurement_unknown',
    'measurement_stale', 'clock_invalid', 'work_deadline', 'total_deadline',
    'log_soft_limit', 'log_ceiling', 'capture_io', 'trx_soft_limit',
    'trx_ceiling', 'source_changed', 'copy_io', 'unsafe_source', 'evidence_incomplete',
})


def _integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _finite(value):
    try:
        return type(value) in (int, float) and math.isfinite(value)
    except (OverflowError, ValueError):
        return False


def _limits(soft, limit, chunk):
    if not (_integer(soft, 1) and _integer(limit, 1) and soft < limit
            and _integer(chunk, 1) and chunk <= 65536):
        raise ValueError('INVALID_BYTE_LIMITS')


def _name(value):
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}', value):
        raise ValueError('INVALID_PRIVATE_NAME')
    return value


def _directory(path):
    """Anchor every component without following symlinks; never chmod inputs."""
    path = Path(path)
    if not path.is_absolute() or '..' in path.parts:
        raise ValueError('ABSOLUTE_PRIVATE_DIRECTORY_REQUIRED')
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    fd = os.open('/', flags)
    try:
        for part in path.parts[1:]:
            child = os.open(part, flags, dir_fd=fd)
            os.close(fd)
            fd = child
        row = os.fstat(fd)
        if row.st_uid != os.geteuid() or stat.S_IMODE(row.st_mode) != 0o700:
            raise ValueError('PRIVATE_DIRECTORY_OWNER_MODE_REQUIRED')
        return fd
    except BaseException:
        os.close(fd)
        raise


def _exclusive(directory, name):
    fd = os.open(_name(name), os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                 0o600, dir_fd=directory)
    try:
        os.fchmod(fd, 0o600)
        return fd
    except BaseException:
        os.close(fd)
        raise


def _identity(row):
    return (row.st_dev, row.st_ino, row.st_size, row.st_mtime_ns,
            row.st_ctime_ns, row.st_nlink, row.st_mode, row.st_uid)


def _check_output(directory, name, fd, size):
    row = os.fstat(fd)
    named = os.stat(name, dir_fd=directory, follow_symlinks=False)
    if (not stat.S_ISREG(row.st_mode) or row.st_nlink != 1
            or row.st_uid != os.geteuid() or stat.S_IMODE(row.st_mode) != 0o600
            or row.st_size != size or _identity(row) != _identity(named)):
        raise OSError('OUTPUT_IDENTITY_CHANGED')
    return _identity(row)


def _verify_output(directory, name, fd, size, expected_digest):
    before = _check_output(directory, name, fd, size)
    os.lseek(fd, 0, os.SEEK_SET)
    digest = hashlib.sha256()
    remaining = size
    while remaining:
        raw = os.read(fd, min(65536, remaining))
        if not raw:
            raise OSError('OUTPUT_SIZE_CHANGED')
        digest.update(raw)
        remaining -= len(raw)
    if os.read(fd, 1) or digest.hexdigest() != expected_digest:
        raise OSError('OUTPUT_CONTENT_CHANGED')
    if _check_output(directory, name, fd, size) != before:
        raise OSError('OUTPUT_CHANGED_DURING_VERIFICATION')
    return before


class StopMachine:
    """Caller-supplied observations only. A decision never authorizes execution."""
    def __init__(self, usage_limits, free_floors, *, started_at, work_seconds,
                 total_seconds, stale_seconds):
        if not (isinstance(usage_limits, dict) and isinstance(free_floors, dict)
                and all(isinstance(k, str) and _integer(v, 1) for k, v in usage_limits.items())
                and all(isinstance(k, str) and _integer(v, 1) for k, v in free_floors.items())
                and all(_finite(v) for v in (started_at, work_seconds, total_seconds, stale_seconds))
                and 0 < work_seconds < total_seconds and stale_seconds > 0):
            raise ValueError('INVALID_OBSERVATION_LIMITS')
        self._usage = dict(usage_limits)
        self._free = dict(free_floors)
        self._start = self._last = started_at
        self._work, self._total, self._stale = work_seconds, total_seconds, stale_seconds
        self._primary = None
        self._secondary = []
        self._finalized = False
        self._lock = threading.RLock()

    def trip(self, reason):
        if reason not in REASONS:
            raise ValueError('INVALID_STOP_REASON')
        with self._lock:
            if self._finalized:
                return
            if self._primary is None:
                self._primary = reason
            elif reason != self._primary and reason not in self._secondary:
                self._secondary.append(reason)

    def observe(self, now, sampled_at, usage, free):
        with self._lock:
            if self._finalized:
                return self.snapshot()
            if not _finite(now) or now < self._last:
                self.trip('clock_invalid')
                return self.snapshot()
            self._last = now
            elapsed = now - self._start
            if elapsed >= self._work:
                self.trip('work_deadline')
            if elapsed >= self._total:
                self.trip('total_deadline')
            if not _finite(sampled_at) or sampled_at < self._start or sampled_at > now:
                self.trip('measurement_unknown')
            elif now - sampled_at > self._stale:
                self.trip('measurement_stale')
            if not (isinstance(usage, dict) and isinstance(free, dict)
                    and usage.keys() == self._usage.keys() and free.keys() == self._free.keys()
                    and all(_integer(v) for v in list(usage.values()) + list(free.values()))):
                self.trip('measurement_unknown')
            else:
                if any(usage[k] >= limit for k, limit in self._usage.items()):
                    self.trip('usage_limit')
                if any(free[k] <= floor for k, floor in self._free.items()):
                    self.trip('reserve_risk')
            return self.snapshot()

    def finalize(self, *, evidence_complete):
        if type(evidence_complete) is not bool:
            raise ValueError('BOOLEAN_COMPLETENESS_REQUIRED')
        with self._lock:
            if not evidence_complete:
                self.trip('evidence_incomplete')
            self._finalized = True
            return self.snapshot()

    def snapshot(self):
        with self._lock:
            return {'state': 'finalized' if self._finalized else 'stop_latched' if self._primary else 'observing',
                    'primary': self._primary, 'secondary': list(self._secondary),
                    'may_advance': not self._finalized and self._primary is None,
                    'outcome': 'failed' if self._primary else 'observation_only',
                    'grants_admission': False}


class LogCapture:
    """Two caller-fed binary channels, one cumulative cap; never read a process."""
    def __init__(self, directory, machine, *, soft_bytes, limit_bytes, chunk_bytes=65536):
        _limits(soft_bytes, limit_bytes, chunk_bytes)
        self._directory = _directory(directory)
        self._machine = machine
        self._soft, self._limit, self._chunk = soft_bytes, limit_bytes, chunk_bytes
        self._files = {}
        self._sizes = {}
        self._digests = {}
        self._identities = {}
        self._written = self._omitted = 0
        self._failed = self._ceiling = self._closed = False
        self._result = None
        self._lock = threading.RLock()

    def feed(self, channel, raw):
        if channel not in ('stdout', 'stderr') or type(raw) is not bytes or len(raw) > self._chunk:
            raise ValueError('INVALID_CAPTURE_CHUNK')
        with self._lock:
            if self._closed:
                raise RuntimeError('CAPTURE_CLOSED')
            accepted = 0
            if raw and not self._failed and self._written < self._limit:
                try:
                    if channel not in self._files:
                        self._files[channel] = _exclusive(self._directory, channel)
                        self._sizes[channel] = 0
                        self._digests[channel] = hashlib.sha256()
                        self._identities[channel] = _identity(os.fstat(self._files[channel]))
                    if _check_output(self._directory, channel, self._files[channel], self._sizes[channel]) != self._identities[channel]:
                        raise OSError('LOG_CHANGED_BETWEEN_CHUNKS')
                    portion = raw[:self._limit - self._written]
                    while accepted < len(portion):
                        count = os.write(self._files[channel], portion[accepted:])
                        if count <= 0 or count > len(portion) - accepted:
                            raise OSError('INVALID_WRITE_PROGRESS')
                        self._digests[channel].update(portion[accepted:accepted + count])
                        accepted += count
                        self._written += count
                        self._sizes[channel] += count
                        self._identities[channel] = _identity(os.fstat(self._files[channel]))
                except OSError:
                    self._failed = True
                    self._machine.trip('capture_io')
            self._omitted += len(raw) - accepted
            if self._written >= self._soft:
                self._machine.trip('log_soft_limit')
            if self._written >= self._limit or (len(raw) > accepted and not self._failed):
                self._ceiling = True
                self._machine.trip('log_ceiling')
            return {'bytes_written': self._written, 'omitted_observed_bytes': self._omitted}

    def finish(self, *, eof):
        if type(eof) is not bool:
            raise ValueError('BOOLEAN_EOF_REQUIRED')
        with self._lock:
            if self._closed:
                return dict(self._result)
            durable = not self._failed
            verified = {}
            for channel, fd in self._files.items():
                try:
                    os.fsync(fd)
                    verified[channel] = _verify_output(self._directory, channel, fd, self._sizes[channel],
                                                       self._digests[channel].hexdigest())
                except OSError:
                    durable = False
                    self._machine.trip('capture_io')
            try:
                os.fsync(self._directory)
            except OSError:
                durable = False
                self._machine.trip('capture_io')
            for channel, fd in self._files.items():
                try:
                    if channel in verified and _check_output(self._directory, channel, fd, self._sizes[channel]) != verified[channel]:
                        raise OSError('OUTPUT_CHANGED_AFTER_VERIFICATION')
                except OSError:
                    durable = False
                    self._machine.trip('capture_io')
                finally:
                    try:
                        os.close(fd)
                    except OSError:
                        durable = False
                        self._machine.trip('capture_io')
            try:
                os.close(self._directory)
            except OSError:
                durable = False
                self._machine.trip('capture_io')
            complete = eof and durable and not self._ceiling and not self._failed
            if not complete:
                self._machine.trip('evidence_incomplete')
            self._closed = True
            self._result = {'bytes_written': self._written, 'omitted_observed_bytes': self._omitted,
                            'omitted_total_bytes': self._omitted if eof else None,
                            'complete': complete, 'durable': durable}
            return dict(self._result)


def copy_trx(source_directory, source_name, destination_directory, destination_name,
             machine, *, soft_bytes, limit_bytes, chunk_bytes=65536):
    """Copy to an exclusive .partial file; receipt confirms bytes, not TRX semantics.

    Source must already be closed by its producer. Identity/size/time checks can
    detect observed changes, not prove a coherent snapshot against a hostile
    concurrent writer. No polling, lifecycle callbacks, deletion or promotion.
    """
    _limits(soft_bytes, limit_bytes, chunk_bytes)
    _name(source_name)
    partial_name = _name(_name(destination_name) + '.partial')
    descriptors = []
    written = 0
    digest = hashlib.sha256()
    status = 'copy_io'
    durable = False
    try:
        source_dir = _directory(source_directory)
        descriptors.append(source_dir)
        source = os.open(source_name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=source_dir)
        descriptors.append(source)
        before = os.fstat(source)
        if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
                or before.st_uid != os.geteuid() or before.st_mode & 0o022):
            status = 'unsafe_source'
            machine.trip('unsafe_source')
        elif before.st_size > limit_bytes:
            status = 'source_too_large'
            machine.trip('trx_ceiling')
        else:
            destination_dir = _directory(destination_directory)
            descriptors.append(destination_dir)
            destination = _exclusive(destination_dir, partial_name)
            descriptors.append(destination)
            destination_id = os.fstat(destination)
            changed = False
            while written < before.st_size:
                raw = os.read(source, min(chunk_bytes, before.st_size - written))
                if not raw:
                    changed = True
                    break
                offset = 0
                while offset < len(raw):
                    count = os.write(destination, raw[offset:])
                    if count <= 0 or count > len(raw) - offset:
                        raise OSError('INVALID_WRITE_PROGRESS')
                    digest.update(raw[offset:offset + count])
                    written += count
                    offset += count
                if written >= soft_bytes:
                    machine.trip('trx_soft_limit')
            changed |= bool(os.read(source, 1))
            changed |= _identity(os.fstat(source)) != _identity(before)
            changed |= _identity(os.stat(source_name, dir_fd=source_dir, follow_symlinks=False)) != _identity(before)
            current_dest = os.stat(partial_name, dir_fd=destination_dir, follow_symlinks=False)
            if (current_dest.st_dev, current_dest.st_ino) != (destination_id.st_dev, destination_id.st_ino):
                raise OSError('DESTINATION_IDENTITY_CHANGED')
            os.fsync(destination)
            output_identity = _verify_output(destination_dir, partial_name, destination,
                                             written, digest.hexdigest())
            os.fsync(destination_dir)
            if _check_output(destination_dir, partial_name, destination, written) != output_identity:
                raise OSError('OUTPUT_CHANGED_AFTER_VERIFICATION')
            changed |= _identity(os.fstat(source)) != _identity(before)
            changed |= _identity(os.stat(source_name, dir_fd=source_dir, follow_symlinks=False)) != _identity(before)
            durable = True
            status = 'source_changed' if changed else 'copied'
            if changed:
                machine.trip('source_changed')
    except (OSError, ValueError):
        machine.trip('copy_io')
        status = 'copy_io'
    finally:
        for fd in reversed(descriptors):
            try:
                os.close(fd)
            except OSError:
                status, durable = 'copy_io', False
                machine.trip('copy_io')
    return {'status': status, 'complete': status == 'copied' and durable,
            'durable': durable, 'bytes_written': written,
            'sha256': digest.hexdigest(), 'file': partial_name}
