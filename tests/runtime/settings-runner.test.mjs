import {fileURLToPath} from 'node:url';import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs';import os from 'node:os';import path from 'node:path';import {execFile,spawnSync} from 'node:child_process';
import {promisify} from 'node:util';
import {validateOwnedResources,createPrivateWorkspace,toolManifest} from '../../scripts/settings/evidence.mjs';
const wrapper=fs.readFileSync(new URL('../../scripts/check-settings.sh',import.meta.url));
const execAsync = promisify(execFile);
test('delegate preserves locked restore arguments and backend failure without touching local runtime',()=>{const dir=fs.mkdtempSync(path.join(os.tmpdir(),'settings-wrapper-'));try{fs.mkdirSync(path.join(dir,'scripts'));fs.writeFileSync(path.join(dir,'scripts/check-settings.sh'),wrapper);fs.writeFileSync(path.join(dir,'scripts/check-policies.sh'),'#!/bin/bash\nprintf "%s\\n" "$@"\nexit 7\n');const r=spawnSync('bash',[path.join(dir,'scripts/check-settings.sh'),'domain','--filter','boundary'],{encoding:'utf8'});assert.equal(r.status,7);assert.deepEqual(r.stdout.trim().split('\n'),['domain','-p:RestoreLockedMode=true','--filter','boundary']);assert.equal(fs.existsSync(path.join(dir,'.runtime')),false);}finally{fs.rmSync(dir,{recursive:true});}});
test('unsupported mode fails instead of reporting acceptance',()=>{const r=spawnSync('bash',[fileURLToPath(new URL('../../scripts/check-settings.sh',import.meta.url)),'unknown'],{encoding:'utf8'});assert.notEqual(r.status,0);});
test('SettingsRunnerOwnsOnlyItsResources',async()=>{const root=fs.mkdtempSync(path.join(os.tmpdir(),'settings-owned-'));try{const a=await createPrivateWorkspace(root),b=await createPrivateWorkspace(root);assert.notEqual(a.owner,b.owner);assert.notEqual(a.project,b.project);assert.equal(fs.statSync(a.directory).mode&0o777,0o700);assert.equal(fs.statSync(path.join(a.directory,'password')).mode&0o777,0o600);const own={'com.docker.compose.project':a.project,'com.webapi.settings.owner':a.owner};assert.doesNotThrow(()=>validateOwnedResources(a,[own]));assert.throws(()=>validateOwnedResources(a,[{...own,'com.webapi.settings.owner':b.owner}]));assert.throws(()=>validateOwnedResources({...a,project:'webapi-local'},[own]));assert.throws(()=>validateOwnedResources(a,[{...own,'com.docker.compose.project':'webapi-local'}]));}finally{fs.rmSync(root,{recursive:true});}});
import {createHash} from 'node:crypto';const digest=b=>createHash('sha256').update(b).digest('hex'),packageScript=fileURLToPath(new URL('../../scripts/package-settings.py',import.meta.url));
function packageFixture(){const root=fs.mkdtempSync(path.join(os.tmpdir(),'settings-package-'));for(const d of ['src','scripts','docs/evidence/settings/ui','.runtime/local','console/node_modules'])fs.mkdirSync(path.join(root,d),{recursive:true});fs.writeFileSync(path.join(root,'src/example.cs'),'valid actual source');const evidence=path.join(root,'docs/evidence/settings'),source={schemaVersion:1,baseCommit:'0'.repeat(40),classification:'actual-uncommitted-build-source',files:{'src/example.cs':digest('valid actual source')}};const write=(name,value)=>fs.writeFileSync(path.join(evidence,name),JSON.stringify(value,null,2)+'\n');write('source-manifest.json',source);const sourceManifestHash=digest(fs.readFileSync(path.join(evidence,'source-manifest.json'))),tool={schemaVersion:1,files:{},sha256:digest('{}')};write('tool-manifest.json',tool);write('verification.json',{complete:true,project:'fixture-settings',cleanup:{containersRemaining:0,volumesRemaining:0,secretsRemaining:0},sourceManifestHash,toolManifestHash:tool.sha256,checks:['settings-persistence','snapshot-unchanged-before-publish','both-gateways-ack','old-route-still-works','audit-export-disabled'].map(name=>({name,pass:true,evidence:{verified:true}})),finalRelease:{targets:[{acknowledged:true},{acknowledged:true}]}});const pixels=fs.readFileSync(new URL('../../docs/evidence/settings/ui/01-security.png',import.meta.url));const screenshots=Array.from({length:10},(_,i)=>{const file='ui/'+i+'.png';fs.writeFileSync(path.join(evidence,file),pixels);return{file,sha256:digest(pixels),width:1440,height:1100};});write('ui/qa.json',{complete:true,sourceManifestHash,visualReviewComplete:true,toolManifestHash:tool.sha256,checks:[{name:'actual flow',pass:true}],screenshots});write('regression.json',{complete:true,sourceManifestHash,toolManifestHash:tool.sha256,suites:{domain:{pass:true,total:1},integration:{pass:true,total:1},gateway:{pass:true,total:1},console:{pass:true,total:1},runner:{pass:true,total:1}}});fs.writeFileSync(path.join(root,'.runtime/local/password'),'PRIVATE-DO-NOT-PACK');fs.writeFileSync(path.join(root,'console/node_modules/private'),'PRIVATE-DO-NOT-PACK');return {root,evidence,write};}
function pack(f){return spawnSync('python3',[packageScript,'--root',f.root,'--evidence',f.evidence],{encoding:'utf8'});}

test('settings freezes and archives the VSTest adapter and referenced source projects without build caches',async t=>{
  const f=packageFixture();
  t.after(()=>fs.rmSync(f.root,{recursive:true,force:true}));
  const repo=path.dirname(path.dirname(packageScript));
  const logger=['scripts/contracts/test-completion.mjs','scripts/contracts/check-completion.mjs','scripts/contracts/run-with-completion.sh',
    ...['ContractCompletionLogger.cs','WebApi.Contracts.TestLogger.csproj','packages.lock.json'].map(n=>'scripts/contracts/runner/'+n)];
  const source=['third_party/json-everything/src/JsonSchema/JsonSchema.csproj','third_party/unicode-idna/LICENSE.txt',
    'tools/WebApi.PerformanceHost/WebApi.PerformanceHost.csproj'];
  const ignored=['scripts/contracts/runner/obj/generated.cs','scripts/contracts/runner/obj/stale.mjs',
    'tools/Other/Other.csproj'];
  for(const name of [...logger,...source,...ignored]){
    fs.mkdirSync(path.dirname(path.join(f.root,name)),{recursive:true});
    fs.writeFileSync(path.join(f.root,name),fs.existsSync(path.join(repo,name))?fs.readFileSync(path.join(repo,name)):'fixture');
  }
  const tools=await toolManifest(f.root);
  assert.deepEqual(Object.keys(tools.files).sort(),[...logger].sort());
  f.write('tool-manifest.json',tools);
  const read=name=>JSON.parse(fs.readFileSync(path.join(f.evidence,name)));
  const manifest=read('source-manifest.json');
  for(const name of source)manifest.files[name]=digest(fs.readFileSync(path.join(f.root,name)));
  f.write('source-manifest.json',manifest);
  const sourceManifestHash=digest(fs.readFileSync(path.join(f.evidence,'source-manifest.json')));
  for(const name of ['verification.json','ui/qa.json','regression.json']){
    f.write(name,{...read(name),sourceManifestHash,toolManifestHash:tools.sha256});
  }
  const result=pack(f);
  assert.equal(result.status,0,result.stderr);
  const zip=JSON.parse(result.stdout).package;
  const listing=spawnSync('python3',['-c','import json,sys,zipfile; print(json.dumps(zipfile.ZipFile(sys.argv[1]).namelist()))',zip],{encoding:'utf8'});
  assert.equal(listing.status,0,listing.stderr);
  const entries=JSON.parse(listing.stdout);
  assert([...logger,...source].every(name=>entries.includes(name)));
  assert(ignored.every(name=>!entries.includes(name)));
});
test('VerifyFailsOnStaleSourceOrFailedCheck',()=>{const f=packageFixture();try{fs.writeFileSync(path.join(f.root,'src/example.cs'),'stale change');assert.notEqual(pack(f).status,0);fs.writeFileSync(path.join(f.root,'src/example.cs'),'valid actual source');const v=JSON.parse(fs.readFileSync(path.join(f.evidence,'verification.json')));v.checks[0].pass=false;f.write('verification.json',v);assert.notEqual(pack(f).status,0);v.checks[0].pass=true;f.write('verification.json',v);fs.rmSync(path.join(f.evidence,'ui/qa.json'));assert.notEqual(pack(f).status,0);}finally{fs.rmSync(f.root,{recursive:true});}});
test('PackageExcludesSecretsAndOldRuntimeData',()=>{const f=packageFixture();try{const r=pack(f);assert.equal(r.status,0,r.stderr);const manifest=JSON.parse(fs.readFileSync(path.join(f.root,'deliverables/WebAPI_Enterprise_系统设置源码及验收_20261005.manifest.json')));assert(manifest.files['src/example.cs']);assert(Object.keys(manifest.files).every(n=>!n.includes('.runtime')&&!n.includes('node_modules')));const check=spawnSync('python3',['-c','import sys,zipfile;z=zipfile.ZipFile(sys.argv[1]);assert z.testzip() is None;assert all(b"PRIVATE-DO-NOT-PACK" not in z.read(n) for n in z.namelist())',path.join(f.root,'deliverables',manifest.package)],{encoding:'utf8'});assert.equal(check.status,0,check.stderr);}finally{fs.rmSync(f.root,{recursive:true});}});
test('PackageGatesRemainActiveUnderOptimizedPython',()=>{const f=packageFixture();try{fs.writeFileSync(path.join(f.root,'src/example.cs'),'unaccepted source');const r=spawnSync('python3',[packageScript,'--root',f.root,'--evidence',f.evidence],{encoding:'utf8',env:{...process.env,PYTHONOPTIMIZE:'1'}});assert.notEqual(r.status,0);}finally{fs.rmSync(f.root,{recursive:true});}});
test('OldBrowserEvidenceCannotPassNewToolIdentity',()=>{const f=packageFixture();try{const q=JSON.parse(fs.readFileSync(path.join(f.evidence,'ui/qa.json')));q.toolManifestHash='previous-browser-tool';f.write('ui/qa.json',q);assert.notEqual(pack(f).status,0);}finally{fs.rmSync(f.root,{recursive:true});}});

test('settings package requires measured successful cleanup for the accepted project', () => {
  const fixture = packageFixture();
  try {
    const file = path.join(fixture.evidence, 'verification.json');
    const accepted = JSON.parse(fs.readFileSync(file));
    assert.equal(pack(fixture).status, 0);
    for (const cleanup of [
      {...accepted.cleanup, secretsRemaining:1},
      {...accepted.cleanup, secretsRemaining:null},
      {...accepted.cleanup, failed:true},
      {...accepted.cleanup, containersRemaining:null},
    ]) {
      fixture.write('verification.json', {...accepted, cleanup});
      assert.notEqual(pack(fixture).status, 0, JSON.stringify(cleanup));
    }
    const retained = {...accepted, review:{project:accepted.project}};
    delete retained.cleanup;
    fixture.write('verification.json', retained);
    assert.notEqual(pack(fixture).status, 0);
    fixture.write('cleanup.json', {project:'older-project', ...accepted.cleanup});
    assert.notEqual(pack(fixture).status, 0);
    fixture.write('cleanup.json', {project:accepted.project, ...accepted.cleanup});
    assert.equal(pack(fixture).status, 0);
  } finally {
    fs.rmSync(fixture.root, {recursive:true});
  }
});

function publishedSettingsPackage(fixture) {
  const run = pack(fixture);
  assert.equal(run.status, 0, run.stderr);
  const {package: file, sha256} = JSON.parse(run.stdout);
  const manifest = file.replace(/\.zip$/, '.manifest.json');
  assert.equal(digest(fs.readFileSync(file)), sha256);
  assert.equal(JSON.parse(fs.readFileSync(manifest)).packageSha256, sha256);
  return {file, manifest, sha256};
}

test('publishing updated settings evidence preserves both versions and their manifests', t => {
  const fixture = packageFixture();
  t.after(() => fs.rmSync(fixture.root, {recursive:true, force:true}));
  const first = publishedSettingsPackage(fixture);
  const originalManifest = fs.readFileSync(first.manifest);
  fs.mkdirSync(path.join(fixture.root, 'docs/deployment'), {recursive:true});
  fs.writeFileSync(path.join(fixture.root, 'docs/deployment/system-settings-notes.md'), 'New release notes');
  const next = publishedSettingsPackage(fixture);
  assert.notEqual(next.sha256, first.sha256);
  assert.notEqual(next.file, first.file);
  assert.equal(digest(fs.readFileSync(first.file)), first.sha256);
  assert.deepEqual(fs.readFileSync(first.manifest), originalManifest);
  assert.deepEqual(publishedSettingsPackage(fixture), next);
});

test('settings publication keeps an incomplete older delivery intact under concurrent retries', async t => {
  const fixture = packageFixture();
  t.after(() => fs.rmSync(fixture.root, {recursive:true, force:true}));
  const out = path.join(fixture.root, 'deliverables');
  fs.mkdirSync(out);
  const historical = path.join(out, 'WebAPI_Enterprise_系统设置源码及验收_20261005.manifest.json');
  fs.writeFileSync(historical, 'retained historical manifest');
  const args = [packageScript, '--root', fixture.root, '--evidence', fixture.evidence];
  const results = await Promise.all([execAsync('python3', args), execAsync('python3', args)]);
  const packages = results.map(r => JSON.parse(r.stdout));
  assert.deepEqual(packages[0], packages[1]);
  assert.equal(fs.readFileSync(historical, 'utf8'), 'retained historical manifest');
  assert.equal(fs.existsSync(historical.replace('.manifest.json','.zip')), false);
  assert.equal(digest(fs.readFileSync(packages[0].package)), packages[0].sha256);
});
