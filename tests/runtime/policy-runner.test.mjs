import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {spawnSync} from 'node:child_process';

function fixture(t, {mode='domain', wrapper='policies', fault='', productExit=0} = {}) {
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'policy-test-cli-')));
  t.after(() => fs.rmSync(root, {recursive:true, force:true}));
  fs.cpSync(new URL('../../scripts/', import.meta.url), path.join(root,'scripts'), {recursive:true});
  const bin = path.join(root,'bin');
  fs.mkdirSync(bin);
  const log = path.join(bin,'calls.jsonl');
  const preload = path.join(bin,'after-write.cjs');
  fs.writeFileSync(preload, `
    const fs = require('node:fs'), promises = require('node:fs/promises'), path = require('node:path');
    for (const [api, key] of [[fs,'writeFileSync'],[promises,'writeFile']]) {
      const original = api[key];
      function fail(file) {
        if (path.basename(String(file)) === 'postgres-password') throw Error('private-fixture-write-failed');
      }
      api[key] = key === 'writeFileSync' ? function(file,...args) {
        const result=original.call(api,file,...args); fail(file); return result;
      } : async function(file,...args) {
        const result=await original.call(api,file,...args); fail(file); return result;
      };
    }
  `);
  fs.writeFileSync(path.join(bin,'docker'), `#!${process.execPath}
import fs from 'node:fs';import path from 'node:path';
const a=process.argv.slice(2),dir=process.env.WEBAPI_POLICY_TEST_DIRECTORY,project=process.env.WEBAPI_POLICY_TEST_PROJECT,owner=process.env.WEBAPI_POLICY_TEST_OWNER;
const fault=process.env.POLICY_FIXTURE_FAULT,bin=process.env.POLICY_FIXTURE_BIN;
const state=name=>path.join(bin,name),secret=path.join(dir,'postgres-password');
const present=()=>fs.existsSync(state('started'))&&!fs.existsSync(state('down'));
fs.appendFileSync(state('calls.jsonl'),JSON.stringify({args:a,dir,project,owner,secretMode:fs.existsSync(secret)?fs.lstatSync(secret).mode&511:null})+'\\n');
if(a[0]==='volume'&&a[1]==='inspect'&&!a.includes('--format')){
  if(fault==='seed-inspect'){console.error('Docker daemon unavailable');process.exit(25);}
  if(fault==='seed-missing'){console.error('Error: No such volume: local-cache');process.exit(1);}
  process.exit(0);
}
if(a.includes('up')){fs.writeFileSync(state('started'),'');process.exit(fault==='up'?9:0);}
if(a.includes('run')) {
  if(fault==='signal'){
    process.once('SIGTERM',()=>{fs.writeFileSync(state('child-stopped'),'');process.exit(0);});
    process.kill(process.ppid,'SIGTERM'); setTimeout(()=>process.exit(0),750);
  }else process.exit(Number(process.env.POLICY_FIXTURE_PRODUCT_EXIT));
} else if(a.includes('ps')||['volume','network'].includes(a[0])&&a[1]==='ls'){
  if(fault==='inventory')process.exit(25);
  if(present())console.log(a.includes('ps')?'container':a[0]==='volume'?project+'_pg-test':project+'_default');
} else if(a.includes('inspect')){
  if(fault==='inspect')process.exit(24);
  const foreign=fault==='foreign'||fault==='foreign-volume'&&a[0]==='volume'||fault==='foreign-network'&&a[0]==='network';
  console.log(JSON.stringify({'com.docker.compose.project':project,'com.webapi.policies.owner':foreign?'another-owner':owner}));
} else if(a.includes('down')){
  if(fault==='down')process.exit(26);
  if(fault==='secret-stuck'){fs.unlinkSync(secret);fs.mkdirSync(secret);}
  if(fault==='replaced'){
    fs.renameSync(dir,dir+'-original');fs.mkdirSync(dir,{mode:0o700});fs.writeFileSync(secret,'foreign sentinel');
  }
  fs.writeFileSync(state('down'),'');
}
`, {mode:0o700});
  const env = {...process.env, PATH:bin+path.delimiter+process.env.PATH, WEBAPI_NODE:process.execPath,
    COMPOSE_PROJECT_NAME:'webapi-enterprise-local', POLICY_FIXTURE_BIN:bin, POLICY_FIXTURE_FAULT:fault, POLICY_FIXTURE_PRODUCT_EXIT:String(productExit)};
  if(fault==='setup') env.NODE_OPTIONS='--require='+preload;
  const result=spawnSync('bash',['scripts/check-'+wrapper+'.sh',mode,'--filter','FullyQualifiedName~PolicyConfigurationTests'],
    {cwd:root,env,encoding:'utf8',timeout:12000});
  assert.ifError(result.error);
  const rows=fs.existsSync(log)?fs.readFileSync(log,'utf8').trim().split('\n').map(JSON.parse):[];
  const receipt=(result.stdout||'').split('\n').filter(line=>line.startsWith('{"kind":"policy-test-cleanup"')).map(JSON.parse).at(-1);
  const runtime=path.join(root,'.runtime');
  const directory=rows[0]?.dir || (receipt?.project && path.join(runtime,receipt.project)) ||
    (fs.existsSync(runtime) && path.join(runtime,fs.readdirSync(runtime)[0]||''));
  return {root,result,rows,receipt,directory,secret:directory && path.join(directory,'postgres-password'),state:name=>path.join(bin,name)};
}

test('policy domain runner isolates its project and measures credentials and Docker after success', t => {
  const f=fixture(t);assert.equal(f.result.status,0,f.result.stderr);
  for(const row of f.rows.filter(r=>r.args.includes('up')||r.args.includes('run')||r.args.includes('down'))){
    assert.match(row.project,/^webapi-policies-test-/);assert.equal(row.args[row.args.indexOf('-p')+1],row.project);
    assert.equal(row.secretMode,0o600);
  }
  assert.ok(f.rows.some(r=>r.args.includes('FullyQualifiedName~PolicyConfigurationTests')));
  assert.ok(f.rows.some(r=>r.args.includes('down')));
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt?.secretsRemaining,0);
  assert.equal(f.receipt.containersRemaining,0);assert.equal(f.receipt.volumesRemaining,0);
  assert.equal(f.receipt.networksRemaining,0);
  assert.ok(f.rows.find(r=>r.args.includes('run')).args.some(a=>a.includes('/nuget-seed/')));
});

test('the optional absent cache uses direct restore and preserves a product failure',t=>{
  const f=fixture(t,{fault:'seed-missing',productExit:7});
  assert.equal(f.result.status,7);
  assert.ok(f.rows.find(r=>r.args.includes('run')).args.includes('dotnet'));
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt.secretsRemaining,0);
});

test('a Docker seed inspection error before Compose starts still revokes its credential',t=>{
  const f=fixture(t,{fault:'seed-inspect'});
  assert.notEqual(f.result.status,0);
  assert.equal(f.rows.some(r=>r.args.includes('up')),false);
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt.secretsRemaining,0);
  assert.equal(f.receipt.containersRemaining,null);
});

test('a partial Compose startup preserves its failure and measures Docker and credentials after down',t=>{
  const f=fixture(t,{fault:'up'});
  assert.equal(f.result.status,9);
  assert.equal(f.rows.some(r=>r.args.includes('run')),false);
  assert.ok(f.rows.some(r=>r.args.includes('down')));
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt.secretsRemaining,0);
  assert.equal(f.receipt.containersRemaining,0);
});

for(const fault of ['foreign','foreign-volume','foreign-network','inspect','inventory','down']){
  test('policy runner records '+fault+' failure and still unlinks only its own credential', t=>{
    const f=fixture(t,{fault});
    assert.notEqual(f.result.status,0);
    assert.equal(fs.existsSync(f.secret),false);
    assert.equal(f.rows.some(r=>r.args.includes('down')),fault==='down');
    assert.equal(f.receipt?.failed,true);
    assert.equal(f.receipt.secretsRemaining,0);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(f.directory,'cleanup.json'),'utf8')),f.receipt);
    if(fault==='inventory')assert.equal(f.receipt.containersRemaining,null);
    if(fault==='down')assert.equal(f.receipt.containersRemaining,1);
    if(fault.startsWith('foreign'))assert.match(f.result.stderr,/ownership/i);
  });
}

test('failed settings gateway tests preserve their exit code even when Docker down also fails',t=>{
  const f=fixture(t,{wrapper:'settings',mode:'gateway',productExit:7,fault:'down'});
  assert.equal(f.result.status,7);
  assert.ok(f.rows.some(r=>r.args.includes('-p:RestoreLockedMode=true')));
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt?.failed,true);
});

test('credential initialization failure runs file cleanup before Compose exists',t=>{
  const f=fixture(t,{fault:'setup'});
  assert.notEqual(f.result.status,0);
  assert.equal(f.rows.length,0);
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt?.secretsRemaining,0);
  assert.equal(f.receipt.containersRemaining,null);
});

test('domain runner reports an unremovable credential entry from the actual directory',t=>{
  const f=fixture(t,{fault:'secret-stuck'});
  assert.notEqual(f.result.status,0);
  assert.equal(fs.lstatSync(f.secret).isDirectory(),true);
  assert.equal(f.receipt?.secretsRemaining,1);
  assert.equal(f.receipt.failed,true);
});

test('domain runner never unlinks a replacement project and reports an unknown named count',t=>{
  const f=fixture(t,{fault:'replaced'});
  assert.notEqual(f.result.status,0);
  assert.equal(fs.readFileSync(f.secret,'utf8'),'foreign sentinel');
  assert.equal(fs.existsSync(path.join(f.directory+'-original','postgres-password')),false);
  assert.equal(f.receipt?.secretsRemaining,null);
  assert.equal(f.receipt.failed,true);
});

test('a real SIGTERM closes the active Docker child and exits 143 after measured cleanup',t=>{
  const f=fixture(t,{mode:'integration',fault:'signal'});
  assert.equal(f.result.status,143);
  assert.equal(fs.existsSync(f.state('child-stopped')),true);
  assert.equal(fs.existsSync(f.secret),false);
  assert.equal(f.receipt?.secretsRemaining,0);
});

test('unsupported mode does not invoke Docker',t=>{
  const f=fixture(t,{mode:'not-a-mode'});assert.equal(f.result.status,2);assert.equal(f.rows.length,0);
});
