#!/usr/bin/env python3
import argparse,hashlib,json,zipfile,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[1]
a=argparse.ArgumentParser();a.add_argument('--output',required=True);args=a.parse_args();output=Path(args.output).resolve();assert not output.exists(),'Refuse to overwrite existing delivery';subprocess.run(['python3',str(root/'scripts/verify-policies-delivery.py')],check=True)
exclude={'.git','.runtime','.secrets','.worktrees','.superpowers','node_modules','bin','obj','dist','__pycache__'};files=[]
for p in root.rglob('*'):
 rel=p.relative_to(root)
 if any(part in exclude for part in rel.parts) or p.is_symlink() or not p.is_file():continue
 if rel.parts[0] not in {'src','tests','console','deploy','scripts','docs'} and p.name not in {'README.md','NuGet.config','global.json','.gitignore','.env.example'} and p.suffix not in {'.sln','.slnx','.props','.targets'}:continue
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
files.sort(key=lambda item:item[1]);output.parent.mkdir(parents=True,exist_ok=True);manifest={}
with zipfile.ZipFile(output,'x',zipfile.ZIP_DEFLATED) as archive:
 for p,name in files:
  data=p.read_bytes();archive.writestr('WebAPI_Enterprise_流量策略/'+name,data);manifest[name]=hashlib.sha256(data).hexdigest()
with zipfile.ZipFile(output) as archive:
 assert archive.testzip() is None
 for name,expected in manifest.items():assert hashlib.sha256(archive.read('WebAPI_Enterprise_流量策略/'+name)).hexdigest()==expected
result={'archive':output.name,'sha256':hashlib.sha256(output.read_bytes()).hexdigest(),'files':manifest,'sourceManifestSha256':manifest['docs/evidence/policies/source-manifest.json']}
output.with_suffix('.manifest.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');print('PASS: '+output.name+'; '+str(len(files))+' files; '+result['sha256'])
