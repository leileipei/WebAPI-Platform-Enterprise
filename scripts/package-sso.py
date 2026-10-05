#!/usr/bin/env python3
"""Freeze reviewed SSO source and public evidence, with explicit uncommitted provenance."""
import argparse,hashlib,json,pathlib,zipfile,os,subprocess
ROOT=pathlib.Path(__file__).resolve().parent.parent
sha=lambda data:hashlib.sha256(data).hexdigest()
def read_json(path):return json.loads(path.read_text())
def validate_archive(package):
    manifest=read_json(pathlib.Path(str(package)+'.manifest.json'))
    with zipfile.ZipFile(package) as archive:
        assert archive.testzip() is None,'ZIP CRC failure'
        names=archive.namelist();assert len(names)==len(set(names)),'Duplicate ZIP entry'
        assert set(names)==set(manifest['files'])|{'delivery-manifest.json'},'Unexpected ZIP entry'
        assert read_json(pathlib.Path(str(package)+'.manifest.json'))==json.loads(archive.read('delivery-manifest.json'))
        for name,expected in manifest['files'].items():assert sha(archive.read(name))==expected,name
        for kind,prefix in [('source','source/'),('tool','')]:
            key=kind+'ManifestHash';raw=archive.read('docs/evidence/sso/'+kind+'-manifest.json')
            assert sha(raw)==manifest[key],'Archived identity mismatch: '+kind
            for name,expected in json.loads(raw)['files'].items():assert manifest['files'][prefix+name]==expected,'Archived input mismatch: '+name
        for name in ['verification.json','ui/qa.json','independent-review.json','regression.json']:
            receipt=json.loads(archive.read('docs/evidence/sso/'+name));actual=receipt['image']['sourceManifestHash'] if name=='verification.json' else receipt['sourceManifestHash']
            assert actual==manifest['sourceManifestHash'] and receipt['toolManifestHash']==manifest['toolManifestHash'],'Archived evidence mismatch: '+name
    digest=sha(package.read_bytes());assert pathlib.Path(str(package)+'.sha256').read_text().split()[0]==digest,'ZIP hash changed'
    return {'complete':True,'zipCrcPassed':True,'fileHashesPassed':True,'entries':len(names),'sha256':digest,'classification':manifest['classification'],'productionDeployment':False}
def frozen_files(manifest,pointer,prefix):
    directory=pathlib.Path(pointer['directory']);assert not directory.is_symlink();directory=directory.resolve();directory.relative_to(ROOT)
    files={}
    for name,expected in manifest['files'].items():
        path=pathlib.PurePosixPath(name)
        assert not path.is_absolute() and '..' not in path.parts and '\\' not in name,'Unsafe frozen path'
        target=directory/name;live=ROOT/name
        assert target.is_file() and not target.is_symlink() and live.is_file() and not live.is_symlink(),name
        data=target.read_bytes();assert sha(data)==expected and sha(live.read_bytes())==expected,'Frozen or live inputs changed: '+name
        files[prefix+name]=data
    return files
def public_files():
    ev=ROOT/'docs/evidence/sso'
    source=read_json(ev/'source-manifest.json');tools=read_json(ev/'tool-manifest.json')
    files=frozen_files(source,read_json(ev/'frozen-source-location.json'),'source/')
    files.update(frozen_files(tools,read_json(ev/'frozen-tools-location.json'),''))
    for directory in [ROOT/'docs/deployment',ev,ROOT/'docs/superpowers']:
        for p in directory.rglob('*'):
            if not p.is_file() or p.is_symlink():continue
            name=str(p.relative_to(ROOT))
            if directory==ROOT/'docs/deployment' and not p.name.startswith('sso-oidc-'):continue
            if directory==ROOT/'docs/superpowers' and '2026-10-05-sso-oidc' not in p.name:continue
            if p.name in ['frozen-source-location.json','frozen-tools-location.json','delivery-index.json','final-verification.json','last-failure.json','privacy-scan.json']:continue
            files[name]=p.read_bytes()
    return files
def validate_evidence_bindings(ev,verification,qa,review,regression,cleanup):
    source_hash=sha((ev/'source-manifest.json').read_bytes());tool_hash=sha((ev/'tool-manifest.json').read_bytes())
    for name,receipt in [('runtime',verification),('QA',qa),('review',review),('regression',regression)]:
        actual=receipt['image']['sourceManifestHash'] if name=='runtime' else receipt.get('sourceManifestHash')
        assert actual==source_hash,'Stale source evidence: '+name
        assert receipt.get('toolManifestHash')==tool_hash,'Missing or stale tool evidence: '+name
    assert review.get('independentReviewPasses')==1 and review.get('authorFixPassComplete') is True,'Missing review/fix provenance'
    assert cleanup['project']==verification['project'],'Cleanup belongs to a different fixture'
    return source_hash,tool_hash
def scan_public(cleanup=False):
    pointer=read_json(ROOT/'.runtime/sso-review.json');context=read_json(pathlib.Path(pointer['directory'])/'review-context.json');private=pathlib.Path(context['directory'])
    assert context['root']==str(ROOT) and context['owner']==pointer['owner'] and context['project']=='webapi-sso-test-'+context['owner']
    assert private.parent==ROOT/'.runtime' and not private.is_symlink() and private.stat().st_mode&0o077==0
    secrets=[]
    for name in ['postgres-password','admin-password','idp-password','idp-admin-password','client-secret','correct-secret']:
        p=private/name
        if p.exists():assert not p.is_symlink();secrets.append(p.read_bytes().strip())
    def realm_values(value):
        if isinstance(value,dict):
            for key,v in value.items():
                if key in ('secret','value') and isinstance(v,str):secrets.append(v.encode())
                else:realm_values(v)
        elif isinstance(value,list):
            for v in value:realm_values(v)
    realm_values(read_json(private/'realm.json'));secrets=[s for s in set(secrets) if len(s)>=8];assert len(secrets)>=5
    if cleanup:
        node=os.environ.get('WEBAPI_NODE');assert node and pathlib.Path(node).is_file(),'Explicit owned-test Node runtime required'
        subprocess.run([node,str(ROOT/'scripts/sso/cleanup.mjs')],cwd=ROOT,check=True)
    files=public_files()
    for name,data in files.items():assert not any(secret in data for secret in secrets),'Secret detected in public payload: '+name
    receipt={'complete':True,'plaintextSecretsFound':0,'privateValuesChecked':len(secrets),'files':{name:sha(data) for name,data in sorted(files.items())}}
    (ROOT/'docs/evidence/sso/privacy-scan.json').write_text(json.dumps(receipt,ensure_ascii=False,indent=2)+'\n');return {'complete':True,'plaintextSecretsFound':0,'publicFilesChecked':len(files),'privateValuesChecked':len(secrets)}
def create_package():
    ev=ROOT/'docs/evidence/sso';verification=read_json(ev/'verification.json');qa=read_json(ev/'ui/qa.json');review=read_json(ev/'independent-review.json');regression=read_json(ev/'regression.json');cleanup=read_json(ev/'cleanup.json');source=read_json(ev/'source-manifest.json')
    assert verification['complete'] and qa['complete'] and qa['visualReviewComplete'] and review['complete'] and review['blockingFindings']==0 and regression['complete']
    assert cleanup['containersRemaining']==cleanup['volumesRemaining']==cleanup['secretsRemaining']==0
    source_hash,tool_hash=validate_evidence_bindings(ev,verification,qa,review,regression,cleanup)
    files=public_files();privacy=read_json(ev/'privacy-scan.json');assert privacy['complete'] and privacy['plaintextSecretsFound']==0
    assert privacy['files']=={name:sha(data) for name,data in sorted(files.items())},'Public payload changed after private scan'
    files['docs/evidence/sso/privacy-scan.json']=(ev/'privacy-scan.json').read_bytes()
    manifest={'schemaVersion':1,'classification':'actual-uncommitted-build-source','baseCommit':source['baseCommit'],'sourceManifestHash':source_hash,'toolManifestHash':tool_hash,'image':verification['image'],'productionDeployment':False,'evidence':{name:True for name in ['realIdp','browser','visualReview','independentReview','regression','cleanup']},'files':{name:sha(data) for name,data in sorted(files.items())}}
    # Scan exact private fixture values before cleanup via a private, hash-only recorded scan receipt.
    out=ROOT/'deliverables';out.mkdir(exist_ok=True);package=out/'WebAPI_Enterprise_SSO_OIDC_20261005.zip';assert not package.exists(),'Never overwrite a sealed delivery'
    with zipfile.ZipFile(package,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
        for name,data in sorted(files.items()):archive.writestr(name,data)
        archive.writestr('delivery-manifest.json',json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    pathlib.Path(str(package)+'.manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n');pathlib.Path(str(package)+'.sha256').write_text(sha(package.read_bytes())+'  '+package.name+'\n')
    result=validate_archive(package);(ev/'delivery-index.json').write_text(json.dumps({'packagePath':str(package),'sha256':result['sha256']},indent=2)+'\n');return result
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--verify',type=pathlib.Path);parser.add_argument('--scan',action='store_true');parser.add_argument('--cleanup-and-scan',action='store_true');args=parser.parse_args();print(json.dumps(validate_archive(args.verify) if args.verify else scan_public(cleanup=args.cleanup_and_scan) if args.scan or args.cleanup_and_scan else create_package(),ensure_ascii=False,indent=2))
