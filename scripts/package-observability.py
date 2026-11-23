#!/usr/bin/env python3
# coding: utf-8
"""Immutable tracked source + rebuilt console. Keeps the core deliverable intact."""
import argparse, hashlib, json, os, pathlib, subprocess, tempfile, zipfile
from observability_delivery import BLOCKED, EVIDENCE, acceptance, commit, require, source_files

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
node = os.environ.get('WEBAPI_NODE', '/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node')
for name in ['console/package.json', 'console/pnpm-lock.yaml']:
    require(source[name] == (root / name).read_bytes(), 'Install the archived frozen dependencies before building')
static = {}
with tempfile.TemporaryDirectory(prefix='webapi-observability-package-') as temporary:
    checkout = pathlib.Path(temporary)
    for name, content in source.items():
        if name.startswith('console/'):
            target = checkout / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
    console = checkout / 'console'
    (console / 'node_modules').symlink_to(root / 'console/node_modules', target_is_directory=True)
    env = dict(os.environ, PATH=str(pathlib.Path(node).parent) + os.pathsep + os.environ['PATH'])
    # pnpm exec may try to purge a linked modules directory in a relocated checkout.
    # Invoke the already-installed locked tool entrypoints without dependency mutation.
    subprocess.run([node, str(root / 'console/node_modules/typescript/bin/tsc'), '--noEmit'], cwd=console, env=env, check=True)
    subprocess.run([node, str(root / 'console/node_modules/vite/bin/vite.js'), 'build'], cwd=console, env=env, check=True)
    for file in sorted((console / 'dist').rglob('*')):
        if file.is_file():
            static['console/dist/' + str(file.relative_to(console / 'dist'))] = file.read_bytes()
require('console/dist/index.html' in static, 'Missing rebuilt console/dist/index.html')
files = {'enterprise/' + name: data for name, data in (source | static).items()}
files.update(attachments)
out = root / 'deliverables'
out.mkdir(exist_ok=True)
archive = out / 'WebAPI_Enterprise_可观测与告警源码及验收.zip'
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
manifest = {'schemaVersion': 2, 'file': archive.name, 'sourceRevision': revision,
            'acceptanceEvidence': evidence, 'acceptanceComplete': evidence['status'] == 'complete',
            'sha256': hashlib.sha256(archive.read_bytes()).hexdigest(), 'files': len(files),
            'secretsExcluded': True, 'archiveIntegrityVerified': True,
            'consoleBuiltFromImmutableSource': True,
            'staticFiles': {name: hashlib.sha256(content).hexdigest() for name, content in sorted(static.items())}}
(out / 'manifest-observability.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
print(str(archive)); print('Files: ' + str(len(files)) + '; SHA256: ' + manifest['sha256'])
