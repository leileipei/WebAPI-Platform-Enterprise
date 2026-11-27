#!/usr/bin/env python3
import argparse,hashlib,json,os,zipfile,subprocess,tempfile
from pathlib import Path
from archive_publication import publish_archive
root=Path(__file__).resolve().parents[1]
a=argparse.ArgumentParser();a.add_argument('--output',required=True);args=a.parse_args()
output=Path(os.path.abspath(args.output))
if os.path.lexists(output):
 raise FileExistsError('Refuse to overwrite existing delivery: '+output.name)
subprocess.run(['python3',str(root/'scripts/verify-policies-delivery.py')],check=True)
exclude={'.git','.runtime','.secrets','.worktrees','.superpowers','node_modules','bin','obj','dist','__pycache__'};files=[]
for p in root.rglob('*'):
 rel=p.relative_to(root)
 if p==output.with_suffix('.manifest.json') or any(part.startswith('.policy-package-') for part in rel.parts):continue
 if any(part in exclude for part in rel.parts) or p.is_symlink() or not p.is_file():continue
 backend_dependency=rel.parts[0]=='third_party' or rel.parts[:2]==('tools','WebApi.PerformanceHost')
 if rel.parts[0] in {'third_party','tools'} and not backend_dependency:continue
 if not backend_dependency and rel.parts[0] not in {'src','tests','console','deploy','scripts','docs'} and p.name not in {'README.md','NuGet.config','global.json','.gitignore','.env.example'} and p.suffix not in {'.sln','.slnx','.props','.targets'}:continue
 if p.suffix=='.zip' or p.name.startswith('operations.log'):continue
 files.append((p,rel.as_posix()))
secret_values=[]
for private in (root/'.runtime').glob('webapi-enterprise-e2e-policies-*'):
 for name in ['password','postgres-password','node-a','node-b','ip-hmac','cursor-key','credential']:
  p=private/name
  if p.is_file():
   value=p.read_bytes().strip()
   if len(value)>=24:secret_values.append(value)
for p,name in files:
 data=p.read_bytes()
 assert all(secret not in data for secret in secret_values),'Live secret in archive candidate: '+name
files.sort(key=lambda item:item[1])
output.parent.mkdir(parents=True,exist_ok=True)
manifest={}
with tempfile.TemporaryDirectory(prefix='.policy-package-',dir=output.parent) as temporary:
    staged=Path(temporary)/output.name
    with zipfile.ZipFile(staged,'x') as archive:
        for p,name in files:
            data=p.read_bytes()
            archive.writestr(zipfile.ZipInfo('WebAPI_Enterprise_流量策略/'+name),data,zipfile.ZIP_DEFLATED)
            manifest[name]=hashlib.sha256(data).hexdigest()
    with zipfile.ZipFile(staged) as archive:
        assert archive.testzip() is None
        for name,expected in manifest.items():
            assert hashlib.sha256(archive.read('WebAPI_Enterprise_流量策略/'+name)).hexdigest()==expected
    result={'archive':output.name,'sha256':hashlib.sha256(staged.read_bytes()).hexdigest(),'files':manifest,'sourceManifestSha256':manifest['docs/evidence/policies/source-manifest.json']}
    companion=staged.with_suffix('.manifest.json')
    companion.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    publish_archive(output.parent,staged,[companion])
print('PASS: '+output.name+'; '+str(len(files))+' files; '+result['sha256'])
