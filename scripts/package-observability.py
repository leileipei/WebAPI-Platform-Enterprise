#!/usr/bin/env python3
# coding: utf-8
"""Immutable tracked source + rebuilt console. Keeps the core deliverable intact."""
import argparse, hashlib, json, os, pathlib, tempfile, zipfile
from observability_delivery import ARCHIVE_STEM, BLOCKED, EVIDENCE, acceptance, commit, require, source_files, production_static_files

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('revision', help='Immutable source revision')
parser.add_argument('--evidence-revision', help='Commit containing the sealed observability receipt')
args = parser.parse_args()
root = pathlib.Path.cwd()
revision = commit(args.revision)
evidence_revision = commit(args.evidence_revision) if args.evidence_revision else revision
source = source_files(revision)
required = ['README.md', 'console/src/coverage.json', 'docs/console-coverage.md',
            'docs/observability-data-dictionary.md', 'docs/deployment/observability-runbook.md',
            'docs/evidence/observability/final-review.md', 'docs/evidence/observability/verification.json',
            'docs/evidence/observability/execution-ledger.md', 'src/WebApi.Infrastructure/Observability/CollectorSignalCoverage.cs']
require(all(p in source for p in required), 'Required delivery material missing')
material = source if evidence_revision == revision else source_files(evidence_revision, EVIDENCE)
evidence, attachments = acceptance(revision, evidence_revision, material)
static = production_static_files(source)
files = {'enterprise/' + name: data for name, data in (source | static).items()}
files.update(attachments)
out = root / 'deliverables'
out.mkdir(exist_ok=True)
# Publish immutable bytes before atomically switching the manifest. Earlier manifests
# keep a readable archive even when different revisions finish packing concurrently.
with tempfile.TemporaryDirectory(dir=out, prefix='.observability-') as temporary:
    staging = pathlib.Path(temporary)
    archive = staging / 'archive.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for name, content in sorted(files.items()):
            entry = zipfile.ZipInfo(name, (2026, 10, 4, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = (0o100755 if name.endswith('.sh') else 0o100644) << 16
            z.writestr(entry, content)
    with zipfile.ZipFile(archive) as z:
        require(z.testzip() is None and len(z.namelist()) == len(files), 'Invalid observability ZIP')
        for name, content in files.items():
            require(z.read(name) == content, f'Archive differs from immutable input: {name}')
        require(not any(BLOCKED.intersection(pathlib.PurePosixPath(n).parts) for n in z.namelist()), 'Excluded ZIP entry')
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    destination = out / f'{ARCHIVE_STEM}-{digest}.zip'
    os.replace(archive, destination)
    manifest = {'schemaVersion': 3, 'file': destination.name, 'sourceRevision': revision,
                'acceptanceEvidence': evidence, 'acceptanceComplete': evidence['status'] == 'complete',
                'sha256': digest, 'files': len(files),
                'secretsExcluded': True, 'archiveIntegrityVerified': True,
                'consoleBuiltFromImmutableSource': True,
                'staticFiles': {name: hashlib.sha256(content).hexdigest() for name, content in sorted(static.items())}}
    next_manifest = staging / 'manifest.json'
    next_manifest.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    os.replace(next_manifest, out / 'manifest-observability.json')
print(str(destination))
print(f"Files: {len(files)}; SHA256: {digest}")
