"""Private stage evidence and explanatory event classes; no admission policy."""
import collections
import datetime
import hashlib
import json
from pathlib import Path
import re

from .private_files import write_private_json_exclusive

KINDS = ('containers', 'volumes', 'networks')
SAFE_CODES = {
    'DUPLICATE_METADATA_ID', 'UNEXPECTED_RESOURCE_KIND', 'INCOMPLETE_INVENTORY_KINDS',
    'INVALID_RESOURCE_IDS', 'DUPLICATE_RESOURCE_ID', 'NON_FULL_RESOURCE_ID',
    'INCOMPLETE_METADATA_KINDS', 'INCOMPLETE_METADATA_IDS',
    'T0_ALREADY_CONSUMED_NO_RECAPTURE', 'T0_INVENTORY_CHANGED', 'T0_METADATA_CHANGED',
    'T0_EVENT_COVERAGE_UNKNOWN', 'T0_EVENT_ROWS_MISSING', 'T0_RESOURCE_EVENT_DURING_BRACKET',
    'CANNOT_READ_COMPLETE_METADATA', 'BOUNDED_EVENT_QUERY_FAILED',
    'PROTECTED_ID_MISSING', 'PROTECTED_METADATA_CHANGED', 'OBSERVER_NOT_HEALTHY',
}
IDENTITY_SIGNALS = {'create', 'destroy', 'remove', 'rename', 'update', 'connect', 'disconnect'}
ACTIVITY = {'exec_create', 'exec_start', 'exec_die', 'health_status'}
KNOWN_ACTIONS = IDENTITY_SIGNALS | ACTIVITY
STAGES = {'start', 'inventory-A', 'metadata-A', 'inventory-B', 'metadata-B', 'inventory-C', 'events'}


def sha(value):
    return hashlib.sha256(value).hexdigest()


def safe_error_fields(error):
    candidate = error.args[0] if type(error) is RuntimeError and len(error.args) == 1 else None
    code = candidate if isinstance(candidate, str) and candidate in SAFE_CODES else 'T0_STAGE_CALLBACK_FAILED'
    return {'type': type(error).__name__, 'code': code}


def owner_labels(value):
    return {k: v for k, v in (value if isinstance(value, dict) else {}).items() if isinstance(k, str) and k.startswith(('com.webapi.', 'com.docker.compose.'))}


def project_metadata(value, budget):
    out = {}
    for kind in KINDS:
        out[kind] = {}
        for identity, row in value[kind].items():
            budget()
            if not isinstance(row, dict):
                out[kind][identity] = {'unavailable': True, 'inputType': type(row).__name__}
                continue
            if kind == 'containers':
                result = {k: row.get(k) for k in ['Name', 'Created', 'Image', 'AutoRemove', 'NetworkMode']}
                result['Mounts'] = [{k: m.get(k) for k in ['Type', 'Source', 'Destination', 'RW', 'Mode']} for m in row.get('Mounts', []) if isinstance(m, dict)]
                command = row.get('CommandSha256')
                result['CommandSha256'] = command if isinstance(command, str) and re.fullmatch('[0-9a-f]{64}', command) else None
            elif kind == 'volumes':
                result = {k: row.get(k) for k in ['CreatedAt', 'Driver', 'Scope']}
            else:
                result = {k: row.get(k) for k in ['Name', 'Created', 'Driver', 'Internal']}
                ipam = row.get('IPAM') or {}
                result['IPAM'] = {'Driver': ipam.get('Driver'), 'Config': [{k: item.get(k) for k in ['Subnet', 'Gateway', 'IPRange']} for item in (ipam.get('Config') or []) if isinstance(item, dict)]}
            result['ownershipLabels'] = owner_labels(row.get('ownershipLabels'))
            out[kind][identity] = result
    return out


def project_event(row):
    if not isinstance(row, dict):
        return {'type': 'unknown-type', 'action': 'unknown-action', 'id': None, 'timeNano': None, 'category': 'unknown'}
    kind, action, identity = row.get('type'), row.get('action'), row.get('id')
    action = action.split(':', 1)[0] if isinstance(action, str) else None
    resource = kind in ['container', 'volume', 'network']
    category = 'identity-related-signal' if resource and action in IDENTITY_SIGNALS else ('execution-activity' if kind == 'container' and action in ACTIVITY else 'unknown')
    valid_id = isinstance(identity, str) and (bool(re.fullmatch('[0-9a-f]{64}', identity)) or (kind == 'volume' and bool(re.fullmatch('[A-Za-z0-9][A-Za-z0-9_.-]*', identity))))
    return {'type': kind if resource else 'unknown-type', 'action': action if action in KNOWN_ACTIONS else 'unknown-action',
            'typeSha256': sha(kind.encode()) if isinstance(kind, str) else None,
            'actionSha256': sha(action.encode()) if isinstance(action, str) else None,
            'id': identity if valid_id else None, 'actorIdSha256': sha(identity.encode()) if isinstance(identity, str) else None,
            'timeNano': row.get('timeNano') if type(row.get('timeNano')) is int else None, 'category': category}


def signature(row):
    return (row.get('typeSha256'), row.get('actionSha256'), row.get('actorIdSha256'), row.get('timeNano'))


class T0Diagnostics:
    def __init__(self, directory, stream_path=None, check_budget=None):
        self.directory = Path(directory)
        self.stream_path = Path(stream_path) if stream_path is not None else None
        self.budget = check_budget or (lambda: None)
        self.records = []
        self.created = False
        self.event_summary = None
        self.unconfirmed_file = None

    def ensure_directory(self):
        if not self.created:
            self.budget()
            self.directory.mkdir(mode=0o700)
            self.directory.chmod(0o700)
            self.created = True

    def compare_stream(self, window_rows):
        result = {'status': 'Unknown', 'matchedWindowRows': None, 'unmatchedWindowRows': None, 'partialTrailingLine': False}
        if self.stream_path is None:
            return dict(result, reason='stream-not-supplied')
        try:
            self.budget()
            # Bounded single read, no polling for a match or replay against the engine.
            with self.stream_path.open('rb') as stream:
                raw = stream.read(16 * 1024 * 1024 + 1)
            self.budget()
            if len(raw) > 16 * 1024 * 1024:
                return dict(result, reason='stream-size-bound')
            result['partialTrailingLine'] = bool(raw and not raw.endswith(b'\n'))
            lines = raw.splitlines()
            if result['partialTrailingLine']:
                lines = lines[:-1]
            rows = []
            for line in lines:
                self.budget()
                if line:
                    rows.append(project_event(json.loads(line)))
            missing = collections.Counter(signature(row) for row in window_rows) - collections.Counter(signature(row) for row in rows)
            result.update(status='matched' if not missing and not result['partialTrailingLine'] else 'Unknown', matchedWindowRows=len(window_rows) - sum(missing.values()), unmatchedWindowRows=sum(missing.values()), retainedStreamRows=len(rows))
            return result
        except TimeoutError:
            raise
        except (OSError, ValueError, UnicodeError) as error:
            return dict(result, reason='stream-read-or-parse-failed', errorType=type(error).__name__)

    def event_payload(self, value):
        value = value if isinstance(value, dict) else {}
        raw_rows = value.get('rows')
        rows = []
        if isinstance(raw_rows, list):
            for row in raw_rows:
                self.budget()
                rows.append(project_event(row))
        categories = dict.fromkeys(['identity-related-signal', 'execution-activity', 'unknown'], 0)
        for row in rows:
            categories[row['category']] += 1
        actors = sorted({row['actorIdSha256'] for row in rows if row.get('actorIdSha256')})
        mapping = {actor: 'C' + str(i + 1).zfill(3) for i, actor in enumerate(actors)}
        actors_summary = [{'anonymousActor': mapping[actor], 'actorIdSha256': actor, 'events': sum(row.get('actorIdSha256') == actor for row in rows)} for actor in actors]
        self.event_summary = {'classificationOnly': True, 'grantsAdmission': False, 'resourceEventsStillReject': True,
                              'categories': categories, 'eventCount': len(rows), 'distinctActors': len(actors), 'actors': actors_summary,
                              'actions': dict(collections.Counter(row['type'] + ':' + row['action'] for row in rows)),
                              'owner': 'Unknown', 'healthCheckOrigin': 'Unknown', 'streamComparison': self.compare_stream(rows),
                              'qualification': 'Classes describe signals only; no benignness, ownership, data integrity, atomic snapshot or admission proof.'}
        return {'window': {'completed': value.get('completed') is True, 'retentionSaturation': value.get('retentionSaturation') is True,
                           'rowsAvailable': isinstance(raw_rows, list), 'rows': rows}, 'summary': self.event_summary}

    def record(self, stage, value):
        if stage not in STAGES:
            raise RuntimeError('UNRECOGNIZED_DIAGNOSTIC_STAGE')
        self.budget()
        self.ensure_directory()
        if stage.startswith('metadata-'):
            payload = project_metadata(value, self.budget)
        elif stage.startswith('inventory-'):
            payload = {kind: list(value[kind]) for kind in KINDS}
        elif stage == 'events':
            payload = self.event_payload(value)
        else:
            payload = {'attempt': 1, 'retry': False, 'qualification': 'Stage evidence does not alter historical outcomes.'}
        name = str(len(self.records) + 1).zfill(2) + '-' + stage + '.json'
        doc = {'schema': 1, 'stage': stage, 'recordedAtUTC': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'sealed': False, 'payload': payload}
        self.budget()
        self.unconfirmed_file = {'stage': stage, 'file': name, 'state': 'write-not-confirmed'}
        write_private_json_exclusive(self.directory / name, doc)
        self.budget()
        digest = sha((self.directory / name).read_bytes())
        self.budget()
        self.records.append({'stage': stage, 'file': name, 'sha256': digest})
        self.unconfirmed_file = None

    def finish(self, outcome, stage, error=None):
        self.budget()
        self.ensure_directory()
        doc = {'schema': 1, 'outcome': outcome, 'sealed': False, 'failureStage': stage if error is not None else None,
               'lastStage': stage, 'error': safe_error_fields(error) if error is not None else None,
               'records': list(self.records), 'eventSummary': self.event_summary,
               'unconfirmedStageFile': self.unconfirmed_file,
               'qualification': 'Partial diagnostic evidence only; this manifest never seals T0 or supersedes the orchestrator result. Historical failures and Unknown remain.'}
        write_private_json_exclusive(self.directory / 'manifest.json', doc)
        self.budget()
