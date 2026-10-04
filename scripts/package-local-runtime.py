#!/usr/bin/env python3
"""Package only immutable source and its rebuilt static output."""
import hashlib, io, json, os, pathlib, re, shutil, subprocess, sys, tarfile, tempfile, zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
BLOCKED = {'.git', '.superpowers', '.runtime', '.secrets', 'secrets', 'backup', 'backups',
           'node_modules', 'bin', 'obj', 'TestResults', 'deliverables', '__pycache__'}
PRIVATE_NAMES = {'runtime.json', 'owner.json', 'release.json', 'bootstrap-password', 'credential', 'postgres.dump'}
COMPONENTS = ['src', 'console', 'deploy', 'Directory.Build.props', 'Directory.Packages.props',
              'NuGet.config', 'global.json', 'WebApi.Enterprise.sln']

def sha(content):
    return hashlib.sha256(content).hexdigest()

def forbidden(name):
    path = pathlib.PurePosixPath(name)
    return (path.is_absolute() or '..' in path.parts or any(p in BLOCKED for p in path.parts)
            or path.name in PRIVATE_NAMES or path.name == '.env' or path.name.startswith('.env.')
            or path.suffix in {'.dump', '.tar', '.trx', '.user', '.log'})

def validate_inputs(proof, files, static_revision, source_revision, old_before, old_after):
    if (proof.get('complete') is not True
        or any(proof.get(k, {}).get('passed') is not True for k in ['chain','restart','failure','backupRestore'])
        or proof.get('ui', {}).get('verified') is not True
        or proof.get('preservedOldEnvironment', {}).get('unchanged') is not True
        or any(proof.get('cleanup', {}).get(k) != 0 for k in ['containersRemaining','volumesRemaining','secretFilesRemaining'])):
        raise ValueError('Incomplete actual acceptance')
    for name in files:
        if forbidden(name):
            raise ValueError('Forbidden delivery file: ' + name)
    if static_revision != source_revision or not re.fullmatch('[a-f0-9]{40}', source_revision):
        raise ValueError('Static source mismatch')
    if old_before != old_after:
        raise ValueError('Old delivery changed')

def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)

def read_source(revision):
    source = {}
    with tarfile.open(fileobj=io.BytesIO(git('archive', revision))) as archive:
        for member in archive:
            if not member.isfile():
                continue
            if forbidden(member.name):
                raise ValueError('Forbidden delivery file: ' + member.name)
            if member.size > 64 * 1024 * 1024:
                raise ValueError('Oversized tracked source')
            source[member.name] = archive.extractfile(member).read()
    return source

def old_hashes(expected):
    return {name: sha((ROOT / name).read_bytes()) for name in expected}

def build_static(source):
    node = os.environ.get('WEBAPI_NODE') or shutil.which('node')
    if not node:
        raise ValueError('WEBAPI_NODE is required')
    for name in ['console/package.json', 'console/pnpm-lock.yaml']:
        if source[name] != (ROOT / name).read_bytes():
            raise ValueError('Install the archived frozen frontend dependencies first')
    output = {}
    with tempfile.TemporaryDirectory(prefix='webapi-runtime-package-') as temporary:
        checkout = pathlib.Path(temporary)
        for name, content in source.items():
            if name.startswith('console/') and not name.startswith('console/dist/'):
                target = checkout / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(content)
        console = checkout / 'console'
        (console / 'node_modules').symlink_to(ROOT / 'console/node_modules', target_is_directory=True)
        env = dict(os.environ, PATH=str(pathlib.Path(node).parent) + os.pathsep + os.environ.get('PATH',''))
        subprocess.run([node, str(ROOT / 'console/node_modules/typescript/bin/tsc'), '--noEmit'], cwd=console, env=env, check=True)
        subprocess.run([node, str(ROOT / 'console/node_modules/vite/bin/vite.js'), 'build'], cwd=console, env=env, check=True)
        for file in sorted((console / 'dist').rglob('*')):
            if file.is_file():
                output['console/dist/' + str(file.relative_to(console / 'dist'))] = file.read_bytes()
    if 'console/dist/index.html' not in output:
        raise ValueError('Missing rebuilt console')
    return output

def load_evidence(source, revision):
    proof = json.loads(source['docs/evidence/local-runtime/verification.json'])
    review = json.loads(source['docs/evidence/local-runtime/final-review.json'])
    if not review.get('ready') or review.get('remainingCritical') != 0 or review.get('remainingImportant') != 0:
        raise ValueError('Whole-branch review pending')
    # The actual container image tested earlier is identified separately. A docs/host-tool
    # commit may follow it only if every application, frontend and deployment input is identical.
    if git('diff', proof['sourceRevision'], revision, '--', *COMPONENTS).strip():
        raise ValueError('Application/deployment changed since actual acceptance')
    for name, digest in proof['ui']['screenshots'].items():
        if sha(source[name]) != digest:
            raise ValueError('UI evidence changed')
    tested = json.loads(source['docs/evidence/local-runtime/tested-release.json'])
    if tested['sourceRevision'] != proof['sourceRevision']:
        raise ValueError('Tested image/source mismatch')
    return proof, tested

def package(revision_arg):
    revision = git('rev-parse', revision_arg + '^{commit}').decode().strip()
    source = read_source(revision)
    required = ['docs/evidence/local-runtime/final-review.md', 'docs/evidence/local-runtime/execution-ledger.md',
                'docs/deployment/local-runtime-runbook.md', 'scripts/local-runtime.sh', 'deploy/compose.runtime.yml']
    if not all(name in source for name in required):
        raise ValueError('Required runtime delivery material missing')
    proof, tested = load_evidence(source, revision)
    expected_old = proof['preservedOldEnvironment']['before']['archives']
    validate_inputs(proof, source, revision, revision, expected_old, old_hashes(expected_old))
    static = build_static(source)
    if {name: sha(data) for name, data in static.items()} != tested['staticFiles']:
        raise ValueError('Static output changed since tested image')
    metadata = {'sourceRevision': revision, 'testedImageSourceRevision': tested['sourceRevision'],
                'rebuild': 'Extract, install frozen frontend dependencies, initialize a local Git commit, then local-runtime.sh build --revision HEAD',
                'imageIncluded': False, 'runtimeSecretsIncluded': False}
    files = dict(source, **static)
    files['SOURCE_REVISION'] = (revision + '\n').encode()
    files['source-archive.json'] = (json.dumps(metadata, ensure_ascii=False, indent=2) + '\n').encode()
    out = ROOT / 'deliverables'
    out.mkdir(exist_ok=True)
    archive = out / 'WebAPI_Enterprise_独立本机运行环境源码及验收.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
        for name, content in sorted(files.items()):
            entry = zipfile.ZipInfo('enterprise/' + name, (2026, 10, 4, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = (0o100755 if name.endswith('.sh') else 0o100644) << 16
            z.writestr(entry, content)
    with zipfile.ZipFile(archive) as z:
        if z.testzip() is not None or set(z.namelist()) != {'enterprise/'+n for n in files}:
            raise ValueError('Archive integrity failed')
        for name, content in files.items():
            if z.read('enterprise/'+name) != content:
                raise ValueError('Archive content mismatch')
    if old_hashes(expected_old) != expected_old:
        raise ValueError('Old delivery changed')
    manifest = {'file': archive.name, 'sourceRevision': revision, 'staticSourceRevision': revision,
                'testedImage': tested, 'testedApplicationAndDeploymentInputsUnchanged': True,
                'acceptanceComplete': True, 'reviewVerified': True, 'secretsExcluded': True,
                'sha256': sha(archive.read_bytes()), 'files': len(files),
                'sourceFiles': {name: sha(data) for name,data in sorted(source.items())},
                'staticFiles': {name: sha(data) for name,data in sorted(static.items())},
                'generatedFiles': {name: sha(files[name]) for name in ['SOURCE_REVISION','source-archive.json']},
                'preservedOldArchives': expected_old}
    (out/'manifest-local-runtime.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    print(str(archive)); print('Files: '+str(len(files))+'; SHA256: '+manifest['sha256'])

if __name__ == '__main__':
    package(sys.argv[1])
