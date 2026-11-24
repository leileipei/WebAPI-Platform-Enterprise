import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';import crypto from 'node:crypto';import {execFile} from 'node:child_process';import {promisify} from 'node:util';
const exec=promisify(execFile),root=path.resolve(import.meta.dirname,'../..'),hash=x=>crypto.createHash('sha256').update(x).digest('hex');
const backendTools=[
 'scripts/check-sso.sh','scripts/acceptance-credentials.mjs','scripts/sso/backend-tests.mjs','scripts/sso/owned-run.mjs',
 'scripts/sso/evidence.mjs','scripts/runtime/exec-file.mjs','scripts/runtime/state.mjs','scripts/runtime/restore-intent.mjs',
 'scripts/runtime/restore-target-lock.mjs','scripts/runtime/restore-manifest.mjs','scripts/contracts/test-completion.mjs',
 'scripts/contracts/run-with-completion.sh','scripts/contracts/runner/WebApi.Contracts.TestLogger.csproj',
 'scripts/contracts/runner/ContractCompletionLogger.cs','scripts/contracts/runner/packages.lock.json',
];
async function candidate(){
 const dir=await fs.mkdtemp(path.join(os.tmpdir(),'sso-package-binding-')),ev=dir+'/docs/evidence/sso',frozen=dir+'/frozen-source',tools=dir+'/frozen-tools';
 const write=async(name,data)=>{await fs.mkdir(path.dirname(dir+'/'+name),{recursive:true});await fs.writeFile(dir+'/'+name,data);};
 const json=async(name,value)=>write(name,JSON.stringify(value,null,2)+'\n');
 await write('src/a.cs','reviewed source\n');await write('frozen-source/src/a.cs','reviewed source\n');
 const source={baseCommit:'f'.repeat(40),files:{'src/a.cs':hash('reviewed source\n')}};await json('docs/evidence/sso/source-manifest.json',source);await json('docs/evidence/sso/frozen-source-location.json',{directory:frozen});
 const scripts=[...backendTools,'scripts/check-console.sh','scripts/local-client.mjs','scripts/policies/build-source.mjs','scripts/package-sso.py'];const fileHashes={};
 for(const name of scripts){const data=await fs.readFile(root+'/'+name);await write(name,data);await write('frozen-tools/'+name,data);fileHashes[name]=hash(data);}
 await json('docs/evidence/sso/tool-manifest.json',{files:fileHashes});await json('docs/evidence/sso/frozen-tools-location.json',{directory:tools});
 const sourceManifestHash=hash(await fs.readFile(ev+'/source-manifest.json')),toolManifestHash=hash(await fs.readFile(ev+'/tool-manifest.json'));const bound={complete:true,sourceManifestHash,toolManifestHash};
 await json('docs/evidence/sso/verification.json',{...bound,project:'webapi-sso-test-fixture',image:{sourceManifestHash,baseCommit:source.baseCommit,digest:'sha256:'+'a'.repeat(64)}});
 await json('docs/evidence/sso/ui/qa.json',{...bound,visualReviewComplete:true});await json('docs/evidence/sso/independent-review.json',{...bound,blockingFindings:0,independentReviewPasses:1,independentReviewedSourceManifestHash:sourceManifestHash,authorFixPassComplete:true});await json('docs/evidence/sso/regression.json',bound);
 await json('docs/evidence/sso/cleanup.json',{project:'webapi-sso-test-fixture',containersRemaining:0,volumesRemaining:0,secretsRemaining:0});
 async function receipt(){await exec('python3',['-c',`import runpy,json,hashlib,pathlib; m=runpy.run_path('scripts/package-sso.py',run_name='fixture'); f=m['public_files'](); pathlib.Path('docs/evidence/sso/privacy-scan.json').write_text(json.dumps({'complete':True,'plaintextSecretsFound':0,'files':{k:hashlib.sha256(v).hexdigest() for k,v in f.items()}}))`],{cwd:dir});}
 await receipt();return {dir,ev,json,write,receipt};
}

test('the SSO ZIP backend entry starts from its archived source beside the complete VSTest adapter',async t=>{
 const c=await candidate();
 t.after(()=>fs.rm(c.dir,{recursive:true,force:true}));
 await exec('python3',['scripts/package-sso.py'],{cwd:c.dir});
 const unzipped=path.join(c.dir,'unpacked');
 await exec('python3',['-c','import pathlib,sys,zipfile;zipfile.ZipFile(next(pathlib.Path("deliverables").glob("*.zip"))).extractall(sys.argv[1])',unzipped],{cwd:c.dir});
 const source=path.join(unzipped,'source');
 for(const name of backendTools)assert.equal(hash(await fs.readFile(path.join(source,name))),hash(await fs.readFile(path.join(root,name))));
 const bin=path.join(c.dir,'bin');
 await fs.mkdir(bin);
 await fs.writeFile(path.join(bin,'docker'),'#!/bin/sh\nexit 0\n',{mode:0o700});
 const result=await exec('bash',['scripts/check-sso.sh','domain'],{cwd:source,env:{...process.env,WEBAPI_NODE:process.execPath,PATH:bin+':'+process.env.PATH}}).catch(e=>e);
 assert.equal(result.code,3,result.stderr);
 assert.match(result.stderr,/completion/i);
 assert.equal(JSON.parse(result.stdout.trim().split('\n').at(-1)).secretsRemaining,0);
});
for(const kind of ['valid','stale-review','stale-regression','missing-tool-binding','changed-live-tool','changed-live-source','foreign-cleanup','failed-cleanup','remaining-network','unknown-network','boolean-count','missing-modern-count'])test('actual package entry validates '+kind,async()=>{
 const c=await candidate();try{
  if(kind==='stale-review'||kind==='stale-regression'||kind==='missing-tool-binding'){const file=kind==='stale-review'?'independent-review.json':kind==='stale-regression'?'regression.json':'ui/qa.json',value=JSON.parse(await fs.readFile(c.ev+'/'+file));if(kind==='missing-tool-binding')delete value.toolManifestHash;else value.sourceManifestHash='0'.repeat(64);await c.json('docs/evidence/sso/'+file,value);await c.receipt();}
  if(kind==='changed-live-tool')await c.write('scripts/local-client.mjs','unreviewed replacement tool\n');
  if(kind==='changed-live-source')await c.write('src/a.cs','unreviewed replacement source\n');
  if(kind==='foreign-cleanup'){const value=JSON.parse(await fs.readFile(c.ev+'/cleanup.json'));value.project='webapi-sso-test-foreign';await c.json('docs/evidence/sso/cleanup.json',value);await c.receipt();}
  const cleanupCases={
    'failed-cleanup':{failed:true,networksRemaining:0},
    'remaining-network':{networksRemaining:1},'unknown-network':{networksRemaining:null},
    'boolean-count':{secretsRemaining:false,networksRemaining:0},'missing-modern-count':{schemaVersion:2},
  };
  if(Object.hasOwn(cleanupCases,kind)){const value=JSON.parse(await fs.readFile(c.ev+'/cleanup.json'));await c.json('docs/evidence/sso/cleanup.json',{...value,...cleanupCases[kind]});await c.receipt();}
  const attempt=exec('python3',['scripts/package-sso.py'],{cwd:c.dir});if(kind==='valid'){const r=await attempt;assert.equal(JSON.parse(r.stdout).complete,true);}else await assert.rejects(attempt);
 }finally{await fs.rm(c.dir,{recursive:true,force:true});}
});
