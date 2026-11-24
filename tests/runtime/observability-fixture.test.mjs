import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const script=fileURLToPath(new URL('../../scripts/observability-e2e.mjs',import.meta.url));
const revision='a'.repeat(40);
function fixture(t){
 const cwd=fs.mkdtempSync(path.join(os.tmpdir(),'webapi-obs-fixture-'));
 t.after(()=>fs.rmSync(cwd,{recursive:true,force:true}));
 const project='webapi-enterprise-e2e-observability-'+randomUUID();
 const dir=path.join(cwd,'.runtime','e2e',project);
 fs.mkdirSync(dir,{recursive:true,mode:0o700});
 const docs=path.join(cwd,'docs','evidence','observability');
 fs.mkdirSync(docs,{recursive:true});
 const env={...process.env,WEBAPI_E2E_DIRECTORY:dir,WEBAPI_E2E_PROJECT:project,WEBAPI_SOURCE_REVISION:revision,WEBAPI_OBS_MODE:'e2e'};
 return {project,dir,docs,run:(...args)=>execFileSync(process.execPath,[script,...args],{cwd,env,stdio:'pipe'})};
}
test('a fresh stack has the separate owner-readable login key required by the control plane',t=>{
 const f=fixture(t);f.run('init');
 const key=path.join(f.dir,'login-hmac');
 assert.equal(fs.statSync(key).mode&0o777,0o400);
 assert.equal(Buffer.from(fs.readFileSync(key,'utf8'),'base64').length,32);
 assert.notEqual(fs.readFileSync(key,'utf8'),fs.readFileSync(path.join(f.dir,'ip-hmac'),'utf8'));
 const first=fs.readFileSync(key);assert.throws(()=>f.run('init'));assert.deepEqual(fs.readFileSync(key),first);
});
test('cleanup removes the four rotated test passwords and retains the previous project receipt on early failure',t=>{
 const f=fixture(t);
 const archive=path.join(f.docs,'verification.json');
 const previous=JSON.stringify({project:'prior-project',complete:true});
 fs.writeFileSync(archive,previous);
 for(const name of ['password','login-hmac','password-e2e-admin','password-e2e-reader','password-e2e-approver','password-e2e-security','credential','browser-cookie']){
  fs.writeFileSync(path.join(f.dir,name),'synthetic',{mode:0o600});
 }
 f.run('cleanup','1','0','0','0');
 assert.equal(fs.readFileSync(archive,'utf8'),previous);
 const receipt=JSON.parse(fs.readFileSync(path.join(f.dir,'verification.json'),'utf8'));
 assert.equal(receipt.project,f.project);assert.equal(receipt.sourceRevision,revision);
 assert.equal(receipt.complete,false);assert.equal(receipt.cleanup.exitCode,1);
 assert.equal(receipt.cleanup.secretFilesRemaining,0);
 assert.deepEqual(fs.readdirSync(f.dir),['verification.json']);
});
