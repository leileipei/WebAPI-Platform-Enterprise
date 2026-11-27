#!/usr/bin/env python3
"""Fail closed on stale source, failed checks or unreviewed screenshots; package public artifacts only."""
import argparse, fcntl, hashlib, json, os, pathlib, re, sys, tempfile, zipfile
from acceptance_cleanup import verify_cleanup_receipt

def require(condition, message):
    if not condition:
        raise ValueError(message)

def sha(value):
    return hashlib.sha256(value).hexdigest()

def load(file):
    return json.loads(file.read_text(encoding='utf-8'))
EXCLUDED = {'.git', '.runtime', '.secrets', '.worktrees', '.superpowers', 'node_modules', 'bin', 'obj', 'dist', '__pycache__'}

def source_files(root):
    files = {}
    for p in sorted(root.iterdir()):
        if p.name in EXCLUDED:
            continue
        if p.is_dir() and p.name in {'src', 'tests', 'console', 'deploy', 'third_party', 'tools'}:
            require(not p.is_symlink(), 'Source symlink rejected: ' + p.name)
            for f in p.rglob('*'):
                relative = f.relative_to(root)
                if p.name == 'tools' and relative.parts[1] != 'WebApi.PerformanceHost':
                    continue
                if any((part in EXCLUDED for part in relative.parts)):
                    continue
                if f.is_symlink():
                    raise ValueError('Source symlink rejected: ' + str(relative))
                if f.is_file():
                    files[relative.as_posix()] = sha(f.read_bytes())
        elif p.is_file() and (p.suffix in {'.slnx', '.sln', '.props', '.targets'} or p.name in {'global.json', 'NuGet.Config', 'NuGet.config'}):
            require(not p.is_symlink(), 'Source symlink rejected: ' + p.name)
            files[p.name] = sha(p.read_bytes())
    return files

def tools_files(root):
    logger = root / 'scripts/contracts/runner'
    logger_inputs = {'ContractCompletionLogger.cs', 'WebApi.Contracts.TestLogger.csproj', 'packages.lock.json'}
    return {f.relative_to(root).as_posix(): sha(f.read_bytes()) for f in sorted((root / 'scripts').rglob('*'))
            if f.is_file() and not f.is_symlink() and (f.suffix in {'.mjs', '.sh', '.py'} or f.parent == logger and f.name in logger_inputs)
            and not any(part in EXCLUDED for part in f.relative_to(root).parts)}

def validate(root, evidence):
    source = load(evidence / 'source-manifest.json')
    identity = sha((evidence / 'source-manifest.json').read_bytes())
    tools = load(evidence / 'tool-manifest.json')
    result = load(evidence / 'verification.json')
    verify_cleanup_receipt(result, evidence)
    qa = load(evidence / 'ui/qa.json')
    regression = load(evidence / 'regression.json')
    require(re.fullmatch('[0-9a-f]{40}', source['baseCommit']), 'Invalid base commit')
    require(source['classification'] == 'actual-uncommitted-build-source', 'Delivery validation failed')
    require(source_files(root) == source['files'], 'Actual product source differs from accepted source')
    require(tools_files(root) == tools['files'], 'Acceptance tool source differs')
    require(tools['sha256'] == sha(json.dumps(tools['files'], ensure_ascii=False, separators=(',', ':')).encode()), 'Invalid tool identity')
    for v in [result, qa, regression]:
        require(v['complete'] is True and v['sourceManifestHash'] == identity, 'Incomplete or stale evidence')
    require(result['toolManifestHash'] == regression['toolManifestHash'] == qa.get('toolManifestHash') == tools['sha256'], 'Delivery validation failed')
    for name in ['settings-persistence', 'snapshot-unchanged-before-publish', 'both-gateways-ack', 'old-route-still-works', 'audit-export-disabled']:
        check = next((c for c in result['checks'] if c['name'] == name))
        require(check['pass'] is True and check['evidence'], name)
    require(all((c['pass'] is True for c in result['checks'])) and len(result['finalRelease']['targets']) == 2 and all((t['acknowledged'] is True for t in result['finalRelease']['targets'])), 'Delivery validation failed')
    require(qa['visualReviewComplete'] is True and len(qa['screenshots']) >= 10 and qa['checks'] and all((c['pass'] is True for c in qa['checks'])), 'Browser review incomplete')
    for screen in qa['screenshots']:
        file = pathlib.PurePosixPath(screen['file'])
        require(file.parts[0] == 'ui' and '..' not in file.parts and (not file.is_absolute()), 'Delivery validation failed')
        require(screen['width'] == 1440 and screen['height'] == 1100 and (sha((evidence / str(file)).read_bytes()) == screen['sha256']), 'Screenshot identity changed')
    for name in ['domain', 'integration', 'gateway', 'console', 'runner']:
        require(regression['suites'][name]['pass'] is True and regression['suites'][name]['total'] > 0, 'Regression failed: ' + name)
    return (source, identity, tools)

def publish(staging, output, name, digest):
    sidecar = pathlib.Path(name).with_suffix('.manifest.json').name
    expected = {name: digest, sidecar: sha((staging / sidecar).read_bytes())}

    def matches(path):
        return not path.is_symlink() and path.is_file() and sha(path.read_bytes()) == expected[path.name]

    directory = os.open(output, os.O_RDONLY)
    try:
        fcntl.flock(directory, fcntl.LOCK_EX)
        # Keep the original filenames for a first publication. A retry can complete
        # a partially published pair only if its existing member has the right bytes.
        if all(not os.path.lexists(output / n) or matches(output / n) for n in expected):
            for n in expected:
                if not os.path.lexists(output / n):
                    os.link(staging / n, output / n)
            return output / name

        destination = output / ('settings-' + digest)
        if os.path.lexists(destination):
            require(not destination.is_symlink() and destination.is_dir() and
                    all(matches(destination / n) for n in expected), 'Existing version differs from this delivery')
        else:
            staging.rename(destination)
        return destination / name
    finally:
        os.close(directory)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=pathlib.Path, required=True)
    parser.add_argument('--evidence', type=pathlib.Path, required=True)
    parser.add_argument('--check-only', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    evidence = args.evidence.resolve()
    (source, identity, tools) = validate(root, evidence)
    if args.check_only:
        print('Current source, regression and reviewed browser evidence match.')
        return
    names = set(source['files']) | set(tools['files'])
    for pattern in ['docs/deployment/system-settings*.md', 'docs/superpowers/specs/2026-10-05-system-settings-design.md', 'docs/superpowers/plans/2026-10-05-system-settings-implementation.md', 'docs/evidence/settings/**/*']:
        for f in root.glob(pattern):
            if f.is_file() and (not f.is_symlink()) and (not any((part in EXCLUDED for part in f.relative_to(root).parts))):
                names.add(f.relative_to(root).as_posix())
    files = {n: sha((root / n).read_bytes()) for n in sorted(names)}
    output = root / 'deliverables'
    require(not output.is_symlink(), 'Invalid delivery directory')
    output.mkdir(exist_ok=True)
    name = 'WebAPI_Enterprise_系统设置源码及验收_20261005.zip'
    manifest = {'schemaVersion': 1, 'package': name, 'baseCommit': source['baseCommit'], 'classification': source['classification'], 'sourceManifestHash': identity, 'toolManifestHash': tools['sha256'], 'files': files}
    with tempfile.TemporaryDirectory(prefix='.settings-package-', dir=output) as temporary:
        staging = pathlib.Path(temporary) / 'delivery'
        staging.mkdir()
        archive_path = staging / name
        with zipfile.ZipFile(archive_path, 'x') as archive:
            # ZipInfo uses a fixed timestamp so identical inputs produce identical ZIP bytes.
            for n in files:
                archive.writestr(zipfile.ZipInfo(n), (root / n).read_bytes(), zipfile.ZIP_DEFLATED)
            archive.writestr(zipfile.ZipInfo('MANIFEST.json'), json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', zipfile.ZIP_DEFLATED)
        with zipfile.ZipFile(archive_path) as archive:
            require(archive.testzip() is None, 'Delivery validation failed')
            for n, expected in files.items():
                require(sha(archive.read(n)) == expected, 'Delivery validation failed')
        manifest['packageSha256'] = sha(archive_path.read_bytes())
        archive_path.with_suffix('.manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        target = publish(staging, output, name, manifest['packageSha256'])
    print(json.dumps({'package': str(target), 'sha256': manifest['packageSha256'], 'files': len(files)}, ensure_ascii=False))
if __name__ == '__main__':
    try:
        main()
    except (AssertionError, ValueError, KeyError, OSError, StopIteration) as error:
        print('Delivery rejected: ' + str(error), file=sys.stderr)
        sys.exit(2)
