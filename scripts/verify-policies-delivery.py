#!/usr/bin/env python3
import hashlib,json,struct,zlib
from pathlib import Path
root=Path(__file__).resolve().parents[1]
evidence=root/'docs/evidence/policies'
def sha(b):return hashlib.sha256(b).hexdigest()
def png(path):
 data=path.read_bytes();assert data[:8]==b'\x89PNG\r\n\x1a\n';at=8;raw=b'';dimensions=None
 while at<len(data):
  size=struct.unpack('>I',data[at:at+4])[0];kind=data[at+4:at+8];chunk=data[at+8:at+8+size];crc=struct.unpack('>I',data[at+8+size:at+12+size])[0];assert zlib.crc32(kind+chunk)&0xffffffff==crc
  if kind==b'IHDR':dimensions=struct.unpack('>IIBBBBB',chunk);assert dimensions[2:]==(8,2,0,0,0) or dimensions[2:]==(8,6,0,0,0)
  if kind==b'IDAT':raw+=chunk
  at+=size+12
  if kind==b'IEND':break
 assert at==len(data);w,h,_,color,*_=dimensions;decoded=zlib.decompress(raw);assert len(decoded)==h*(1+w*(4 if color==6 else 3));assert any(decoded);return w,h
v=json.loads((evidence/'verification.json').read_text());assert v['complete'];manifest=(evidence/'source-manifest.json').read_bytes();source=json.loads(manifest);assert sha(manifest)==v['image']['sourceManifestHash'];assert source['baseCommit']==v['image']['baseCommit'];assert v['image']['imageDigest'].startswith('sha256:')
for name,expected in source['files'].items():
 p=root/name;assert p.is_file(),name;assert sha(p.read_bytes())==expected,name;assert not any(part in ['.secrets','.runtime','node_modules','bin','obj','.git'] for part in p.parts)
assert len(v['finalRelease']['targets'])==2 and all(t['acknowledged'] for t in v['finalRelease']['targets']);assert v['finalStatus']==200
by_name={c['name']:c for c in v['checks']};assert len(by_name)>=9 and all(c['passed'] for c in by_name.values());assert by_name['TwoPointZeroAndTwoPointOneRollback']['originalHash']==by_name['TwoPointZeroAndTwoPointOneRollback']['rollbackHash'];assert by_name['SharedRateLimit']['accepted']==3;assert by_name['SharedRateLimit']['rejected']==17
assert by_name['FrozenPolicyPartialRelease']['selectedRuntimeId']!=by_name['FrozenPolicyPartialRelease']['retainedRuntimeId'];assert by_name['ObservationScopeAndSecretExclusion']['ratePolicyId']==v['policies']['rateId'];assert by_name['ObservationScopeAndSecretExclusion']['traceId']==v['signals']['traceId']
timeout_check=by_name['PathEditTimeoutUnbindAndPublish'];assert timeout_check['baseTimeoutMs']==30000 and timeout_check['effectivePolicyTimeoutMs']==1000 and timeout_check['publishedTimeoutMs']==30000 and timeout_check['gatewayStatus']==200
regression=json.loads((evidence/'regression.json').read_text());assert regression['complete'] and regression['dotnet']=={'domain':92,'integration':241,'gateway':51} and regression['consoleNode']==44 and regression['runtimeNode']==59
qa=json.loads((evidence/'ui/qa.json').read_text());assert qa['complete'] and qa['visualReviewComplete'];assert qa['sourceManifestHash']==v['image']['sourceManifestHash'];assert not qa['defects'];assert qa['project']==v['project'] and all(c['passed'] for c in qa['checks']);assert len(qa['screenshots'])>=14
for item in qa['screenshots']:
 p=evidence/item['file'];assert sha(p.read_bytes())==item['sha256'];assert png(p)==(1440,item['height']);assert item['width']==1440
matrix=json.loads((evidence/'coverage-matrix.json').read_text());assert set(matrix)=={'P%02d'%i for i in range(1,20)};assert all(c['complete'] and c['evidence'] for c in matrix.values())
for item in matrix.values():
 for file in item['evidence']:assert (root/file).is_file(),file
retained=json.loads((evidence/'retained-deliveries.json').read_text());
for name,expected in retained['files'].items():
 p=Path(name)
 assert p.is_file(),'Old delivery missing: '+name
 assert sha(p.read_bytes())==expected,'Old delivery changed: '+name
print('PASS: actual source, image identity, business facts, 19 evidence rows, real PNGs and retained archives')
