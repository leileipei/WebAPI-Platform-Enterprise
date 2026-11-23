#!/usr/bin/env python3
# coding: utf-8
"""Verify a completed observability delivery against immutable Git source and receipts."""
import json
from pathlib import Path, PurePosixPath
import zipfile

from observability_delivery import ARCHIVE_STEM, BLOCKED, EVIDENCE, acceptance, git, production_static_files, recorded_commit, require, sha256, source_files


def verify():
    out = Path('deliverables')
    manifest = json.loads((out / 'manifest-observability.json').read_bytes())
    source = recorded_commit(manifest.get('sourceRevision'))
    require(source and source == manifest['sourceRevision'], 'Expected a full immutable source revision')
    claimed = manifest.get('acceptanceEvidence')
    if 'schemaVersion' not in manifest and 'acceptanceEvidence' not in manifest:
        # Historical manifests stored only the source SHA. Their evidence was enclosed in that source.
        evidence_revision = source
    else:
        require(manifest.get('schemaVersion') in (2, 3) and isinstance(claimed, dict), 'Missing acceptance provenance')
        evidence_revision = recorded_commit(claimed.get('revision'))
        require(evidence_revision and evidence_revision == claimed['revision'], 'Invalid evidence revision')
    source_blobs = source_files(source)
    material = source_blobs if source == evidence_revision else source_files(evidence_revision, EVIDENCE)
    evidence, attachments = acceptance(source, evidence_revision, material)
    require(evidence['status'] == 'complete', 'Observability acceptance not established: ' + evidence['status'])
    require(manifest.get('acceptanceComplete') is True, 'Manifest does not claim completed acceptance')
    require(claimed is None or claimed == evidence, 'Manifest provenance differs from the immutable evidence')
    require(all(manifest.get(k) is True for k in ('archiveIntegrityVerified', 'consoleBuiltFromImmutableSource',
                                                   'secretsExcluded')), 'Incomplete delivery manifest')
    expected_filename = (f"{ARCHIVE_STEM}-{manifest['sha256']}.zip" if manifest.get('schemaVersion') == 3
                         else f"{ARCHIVE_STEM}.zip")
    require(isinstance(manifest.get('sha256'), str) and len(manifest['sha256']) == 64
            and all(c in '0123456789abcdef' for c in manifest['sha256']), 'Invalid archive digest')
    require(manifest.get('file') == expected_filename, 'Unexpected archive name')
    archive = out / manifest['file']
    require(sha256(archive.read_bytes()) == manifest['sha256'], 'Archive SHA256 mismatch')
    static = manifest['staticFiles']
    require(isinstance(static, dict) and 'console/dist/index.html' in static and
            all(name.startswith('console/dist/') and '..' not in name.split('/') for name in static), 'Invalid static files')
    # The original sealed manifest was committed together with its production hashes.
    # Other packages must reproduce the production build independently of the packager.
    sealed_path = 'deliverables/manifest-observability.json'
    original_seal = 'df5f4d1f7cabd8d21342d7d2e327e1c34da581d4'
    sealed_legacy = ('schemaVersion' not in manifest and recorded_commit(original_seal)
                     and json.loads(git('show', original_seal + ':' + sealed_path)) == manifest)
    if not sealed_legacy:
        expected = {name: sha256(data) for name, data in production_static_files(source_blobs).items()}
        require(static == expected, 'Static files differ from the reproducible production build')
    with zipfile.ZipFile(archive) as z:
        require(z.testzip() is None and len(z.infolist()) == manifest['files'], 'Invalid ZIP contents')
        files = {'enterprise/' + name: content for name, content in source_blobs.items()}
        files.update(attachments)
        names = set(files) | {'enterprise/' + name for name in static}
        require(len(names) == len(z.infolist()) and set(z.namelist()) == names, 'Unexpected or missing ZIP entries')
        require(not any(BLOCKED.intersection(PurePosixPath(name).parts) for name in names), 'Excluded ZIP entry')
        for name, content in files.items():
            require(z.read(name) == content, f'ZIP differs from Git: {name}')
        for name, digest in static.items():
            require(sha256(z.read('enterprise/' + name)) == digest, f'Static file SHA256 mismatch: {name}')
    print('Immutable source, static build, ZIP integrity, SHA256, required evidence and exclusions verified.')


if __name__ == '__main__':
    try:
        verify()
    except (KeyError, ValueError, OSError, zipfile.BadZipFile) as error:
        raise SystemExit(str(error))
