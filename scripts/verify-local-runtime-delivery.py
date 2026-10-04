#!/usr/bin/env python3
"""Verify the ZIP against the immutable Git source and rebuilt static output."""
import importlib.util, json, pathlib, sys, zipfile
sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('runtime_package',pathlib.Path(__file__).with_name('package-local-runtime.py'))
policy=importlib.util.module_from_spec(spec);spec.loader.exec_module(policy)

def verify(archive,manifest):
    source=policy.read_source(manifest['sourceRevision'])
    proof,tested=policy.load_evidence(source,manifest['sourceRevision'])
    policy.validate_inputs(proof,source,manifest['staticSourceRevision'],manifest['sourceRevision'],
        manifest['preservedOldArchives'],policy.old_hashes(manifest['preservedOldArchives']))
    if policy.sha(archive.read_bytes())!=manifest['sha256']:
        raise ValueError('ZIP SHA mismatch')
    static=policy.build_static(source)
    if manifest['sourceFiles']!={n:policy.sha(b) for n,b in sorted(source.items())}:
        raise ValueError('Immutable source manifest mismatch')
    if manifest['staticFiles']!={n:policy.sha(b) for n,b in sorted(static.items())} or manifest['staticFiles']!=tested['staticFiles']:
        raise ValueError('Static source mismatch')
    hashes=dict(manifest['sourceFiles'],**manifest['staticFiles'],**manifest['generatedFiles'])
    with zipfile.ZipFile(archive) as z:
        names=z.namelist()
        if z.testzip() is not None or len(names)!=len(set(names)) or set(names)!={'enterprise/'+n for n in hashes}:
            raise ValueError('Unexpected or duplicate ZIP entry')
        for name,digest in hashes.items():
            if policy.forbidden(name) or policy.sha(z.read('enterprise/'+name))!=digest:
                raise ValueError('Forbidden or mismatched ZIP entry')
        for name,content in source.items():
            if z.read('enterprise/'+name)!=content:
                raise ValueError('Immutable source bytes mismatch')
        for name,content in static.items():
            if z.read('enterprise/'+name)!=content:
                raise ValueError('Rebuilt static bytes mismatch')
        if z.read('enterprise/SOURCE_REVISION').decode().strip()!=manifest['sourceRevision']:
            raise ValueError('Source metadata mismatch')
    print('PASS: immutable source bytes, rebuilt static bytes, ZIP SHA, private-file exclusion, old ZIP hashes')

if __name__=='__main__':
    verify(pathlib.Path(sys.argv[1]),json.loads(pathlib.Path(sys.argv[2]).read_text()))
