#!/usr/bin/env python3
# coding: utf-8
"""Immutable tracked source + rebuilt console. Keeps the core deliverable intact."""
import hashlib, io, json, os, pathlib, subprocess, tarfile, tempfile, zipfile, sys
root = pathlib.Path.cwd()
revision = subprocess.check_output(['git', 'rev-parse', sys.argv[1] + '^{commit}'], text=True).strip()
blocked = {'.git', '.superpowers', '.runtime', '.secrets', 'node_modules', 'bin', 'obj', 'TestResults', 'deliverables'}
source = {}
with tarfile.open(fileobj=io.BytesIO(subprocess.check_output(['git', 'archive', revision]))) as tar:
    for member in tar:
        path = pathlib.PurePosixPath(member.name)
        if not member.isfile() or any(p in blocked for p in path.parts):
            continue
        assert not path.is_absolute() and '..' not in path.parts
        assert member.size < 64 * 1024 * 1024
        source[member.name] = tar.extractfile(member).read()
required = ['README.md', 'console/src/coverage.json', 'docs/console-coverage.md',
            'docs/observability-data-dictionary.md', 'docs/deployment/observability-runbook.md',
            'docs/evidence/observability/final-review.md', 'docs/evidence/observability/verification.json',
            'docs/evidence/observability/execution-ledger.md', 'src/WebApi.Infrastructure/Observability/CollectorSignalCoverage.cs']
assert all(p in source for p in required), 'Required delivery material missing'
verified = json.loads(source['docs/evidence/observability/verification.json'])
assert verified['complete'] and verified['cleanup']['secretFilesRemaining'] == 0, 'Acceptance is not sealed'
node = os.environ.get('WEBAPI_NODE', '/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node')
pnpm = os.environ.get('WEBAPI_PNPM', '/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/bin/fallback/pnpm')
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
    subprocess.run([pnpm, 'exec', 'tsc', '--noEmit'], cwd=console, env=env, check=True)
    subprocess.run([pnpm, 'exec', 'vite', 'build'], cwd=console, env=env, check=True)
    for file in sorted((console / 'dist').rglob('*')):
        if file.is_file():
            static['console/dist/' + str(file.relative_to(console / 'dist'))] = file.read_bytes()
assert 'console/dist/index.html' in static
files = source | static
out = root / 'deliverables'
out.mkdir(exist_ok=True)
archive = out / 'WebAPI_Enterprise_可观测与告警源码及验收.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    for name, content in sorted(files.items()):
        entry = zipfile.ZipInfo('enterprise/' + name, (2026, 10, 4, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        entry.external_attr = (0o100755 if name.endswith('.sh') else 0o100644) << 16
        z.writestr(entry, content)
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len(z.namelist()) == len(files)
    for name, content in files.items():
        assert z.read('enterprise/' + name) == content
    assert not any(any(p in blocked for p in pathlib.PurePosixPath(n).parts) for n in z.namelist())
manifest = {'file': archive.name, 'sourceRevision': revision, 'acceptanceComplete': True,
            'sha256': hashlib.sha256(archive.read_bytes()).hexdigest(), 'files': len(files),
            'secretsExcluded': True, 'archiveIntegrityVerified': True,
            'consoleBuiltFromImmutableSource': True,
            'staticFiles': {name: hashlib.sha256(content).hexdigest() for name, content in sorted(static.items())}}
(out / 'manifest-observability.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
print(str(archive)); print('Files: ' + str(len(files)) + '; SHA256: ' + manifest['sha256'])
