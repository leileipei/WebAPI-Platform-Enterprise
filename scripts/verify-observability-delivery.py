#!/usr/bin/env python3
# coding: utf-8
import hashlib, json, pathlib, subprocess, zipfile
root = pathlib.Path.cwd()
manifest = json.loads((root / 'deliverables/manifest-observability.json').read_text())
assert manifest['acceptanceComplete'] and manifest['archiveIntegrityVerified']
assert manifest['consoleBuiltFromImmutableSource'] and manifest['secretsExcluded']
revision = subprocess.check_output(['git', 'rev-parse', manifest['sourceRevision'] + '^{commit}'], text=True).strip()
assert revision == manifest['sourceRevision']
archive = root / 'deliverables' / manifest['file']
assert hashlib.sha256(archive.read_bytes()).hexdigest() == manifest['sha256']
blocked = {'.git', '.superpowers', '.runtime', '.secrets', 'node_modules', 'bin', 'obj', 'TestResults', 'deliverables'}
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None and len(z.namelist()) == manifest['files']
    assert not any(any(p in blocked for p in pathlib.PurePosixPath(n).parts) for n in z.namelist())
    assert json.loads(z.read('enterprise/docs/evidence/observability/verification.json'))['complete']
    for name, sha in manifest['staticFiles'].items():
        assert hashlib.sha256(z.read('enterprise/' + name)).hexdigest() == sha
    tracked = subprocess.check_output(['git', 'ls-tree', '-r', '--name-only', revision], text=True).splitlines()
    for name in tracked:
        if any(p in blocked for p in pathlib.PurePosixPath(name).parts):
            continue
        assert z.read('enterprise/' + name) == subprocess.check_output(['git', 'show', revision + ':' + name])
print('Immutable source, static build, ZIP integrity, SHA256, required evidence and exclusions verified.')
