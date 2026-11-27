import json


def verify_cleanup_receipt(result, evidence_directory):
    records = [result['cleanup']] if 'cleanup' in result else []
    separate = evidence_directory / 'cleanup.json'
    if separate.is_file():
        record = json.loads(separate.read_text(encoding='utf-8'))
        if not isinstance(record, dict):
            raise ValueError('Invalid cleanup receipt')
        if result.get('project') and record.get('project') == result['project']:
            records.append(record)

    if not records:
        raise ValueError('Missing cleanup receipt for this acceptance run')
    for record in records:
        if not isinstance(record, dict) or record.get('failed', False) is not False:
            raise ValueError('Failed cleanup receipt')
        if 'project' in record and record['project'] != result.get('project'):
            raise ValueError('Mismatched cleanup receipt project')
        counts = ['secretsRemaining', 'containersRemaining', 'volumesRemaining']
        if 'networksRemaining' in record:
            counts.append('networksRemaining')
        if any(type(record.get(key)) is not int or record[key] != 0 for key in counts):
            raise ValueError('Incomplete cleanup receipt')
