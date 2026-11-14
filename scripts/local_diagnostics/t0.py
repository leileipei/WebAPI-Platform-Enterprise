"""Read-only snapshot contracts; no engine access or import-time side effects."""
import datetime
import hashlib
import json
import time
from .t0_diagnostics import safe_error_fields

KINDS = ('containers', 'volumes', 'networks')


def require(ok, code):
    if not ok:
        raise RuntimeError(code)


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True)


def digest(value):
    return hashlib.sha256(canonical(value).encode()).hexdigest()


def owner_labels(labels):
    return {k: v for k, v in (labels or {}).items() if k.startswith(('com.webapi.', 'com.docker.compose.'))}


def safe_metadata(kind, rows):
    out = {}
    for x in rows:
        identity = x['Name'] if kind == 'volumes' else x['Id']
        require(identity not in out, 'DUPLICATE_METADATA_ID')
        if kind == 'containers':
            config = x.get('Config') or {}
            host = x.get('HostConfig') or {}
            mounts = [{k: item.get(k) for k in ['Type', 'Source', 'Destination', 'RW', 'Mode']} for item in x.get('Mounts', [])]
            out[identity] = {'Name': x['Name'], 'Created': x['Created'], 'Image': x['Image'],
                             'ownershipLabels': owner_labels(config.get('Labels')),
                             'AutoRemove': host.get('AutoRemove'), 'NetworkMode': host.get('NetworkMode'),
                             'Mounts': sorted(mounts, key=canonical),
                             'CommandSha256': digest([config.get('Entrypoint'), config.get('Cmd')])}
        elif kind == 'volumes':
            out[identity] = {k: x.get(k) for k in ['CreatedAt', 'Driver', 'Scope']}
            out[identity]['ownershipLabels'] = owner_labels(x.get('Labels'))
        elif kind == 'networks':
            out[identity] = {k: x.get(k) for k in ['Name', 'Created', 'Driver', 'Internal']}
            out[identity]['ownershipLabels'] = owner_labels(x.get('Labels'))
            ipam = x.get('IPAM') or {}
            out[identity]['IPAM'] = {'Driver': ipam.get('Driver'), 'Config': sorted([{k: item.get(k) for k in ['Subnet', 'Gateway', 'IPRange']} for item in (ipam.get('Config') or [])], key=canonical)}
        else:
            raise RuntimeError('UNEXPECTED_RESOURCE_KIND')
    return out


def valid_inventory(rows):
    require(set(rows) == set(KINDS), 'INCOMPLETE_INVENTORY_KINDS')
    for kind in KINDS:
        ids = rows[kind]
        require(isinstance(ids, list) and all(isinstance(x, str) and x for x in ids), 'INVALID_RESOURCE_IDS')
        require(len(ids) == len(set(ids)), 'DUPLICATE_RESOURCE_ID')
        if kind != 'volumes':
            require(all(len(x) == 64 and all(c in '0123456789abcdef' for c in x) for x in ids), 'NON_FULL_RESOURCE_ID')
    return {k: sorted(rows[k]) for k in KINDS}


def valid_metadata(ids, meta):
    require(set(meta) == set(KINDS), 'INCOMPLETE_METADATA_KINDS')
    for kind in KINDS:
        require(set(meta[kind]) == set(ids[kind]), 'INCOMPLETE_METADATA_IDS')


def capture_once(state, inventory, metadata, events, healthy, diagnostics=None):
    require(not state.get('t0Attempted'), 'T0_ALREADY_CONSUMED_NO_RECAPTURE')
    state['t0Attempted'] = True
    started = datetime.datetime.now(datetime.timezone.utc).isoformat()
    mono = time.monotonic()
    def stage(name):
        state['lastStage'] = name
    def record(name, value):
        if diagnostics is not None:
            diagnostics.record(name, value)
    try:
        stage('start')
        record('start', None)
        stage('health-before-A')
        healthy()
        stage('inventory-A')
        a = valid_inventory(inventory())
        record('inventory-A', a)
        stage('health-before-metadata-A')
        healthy()
        stage('metadata-A')
        ma = metadata(a)
        valid_metadata(a, ma)
        record('metadata-A', ma)
        stage('health-before-B')
        healthy()
        stage('inventory-B')
        b = valid_inventory(inventory())
        record('inventory-B', b)
        stage('compare-inventory-B')
        require(a == b, 'T0_INVENTORY_CHANGED')
        stage('metadata-B')
        mb = metadata(b)
        valid_metadata(b, mb)
        record('metadata-B', mb)
        stage('compare-metadata-B')
        require(ma == mb, 'T0_METADATA_CHANGED')
        stage('health-before-C')
        healthy()
        stage('inventory-C')
        c = valid_inventory(inventory())
        record('inventory-C', c)
        stage('compare-inventory-C')
        require(b == c, 'T0_INVENTORY_CHANGED')
        ended = datetime.datetime.now(datetime.timezone.utc).isoformat()
        stage('event-window')
        observed = events(started, ended)
        record('events', observed)
        stage('event-gate')
        require(observed.get('completed') is True and observed.get('retentionSaturation') is not True, 'T0_EVENT_COVERAGE_UNKNOWN')
        require(isinstance(observed.get('rows'), list), 'T0_EVENT_ROWS_MISSING')
        require(not any(row.get('type') in ['container', 'volume', 'network'] for row in observed['rows']), 'T0_RESOURCE_EVENT_DURING_BRACKET')
        stage('health-after-events')
        healthy()
        stage('capture-return')
        receipt = {'sealed': True, 'atomicSnapshot': False, 'startedAt': started, 'endedAt': ended,
                   'elapsedSeconds': time.monotonic() - mono, 'inventorySha256': digest(c), 'metadataSha256': digest(mb),
                   'eventCoverage': 'Bounded event query completed with live observer healthy; daemon retention and non-atomic races remain limitations.',
                   'qualification': 'Capture does not supersede earlier failures or authorize resource cleanup.'}
        if diagnostics is not None:
            diagnostics.finish('capture-complete', state['lastStage'])
        return c, mb, receipt
    except BaseException as error:
        fields = safe_error_fields(error)
        state['safeErrorCode'] = fields['code']
        state['errorType'] = fields['type']
        if diagnostics is not None:
            try:
                diagnostics.finish('failed', state['lastStage'], error)
            except BaseException as diagnostic_error:
                state['diagnosticFailureType'] = type(diagnostic_error).__name__
        raise


def verify_protected(before, before_meta, current, current_meta):
    valid_inventory(current)
    valid_metadata(before, current_meta)
    missing = {k: sorted(set(before[k]) - set(current[k])) for k in KINDS}
    changed = {k: [identity for identity, row in before_meta[k].items() if current_meta[k].get(identity) != row] for k in KINDS}
    require(not any(missing.values()), 'PROTECTED_ID_MISSING')
    require(not any(changed.values()), 'PROTECTED_METADATA_CHANGED')
    return {'missingStartingResources': missing, 'changedStartingMetadata': changed,
            'foreignAdditions': {k: sorted(set(current[k]) - set(before[k])) for k in KINDS}}
