import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import test from 'node:test';
import {cleanupSsoFixture, createSsoFixture} from '../../scripts/sso/fixture.mjs';

const exec = promisify(execFile);
const repository = path.resolve(import.meta.dirname, '../..');
const secretFiles = ['postgres-password', 'admin-password', 'idp-password', 'idp-admin-password',
  'client-secret', 'login-hmac', 'correct-secret', 'realm.json', 'fixture-compose.json'];

async function workspace(t) {
  const root = await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(), 'sso-credential-test-')));
  t.after(() => fs.rm(root, {recursive:true, force:true}));
  const owner = randomUUID(), project = 'webapi-sso-test-' + owner;
  const directory = path.join(root, '.runtime', project);
  await fs.mkdir(directory, {mode:0o700, recursive:true});
  await fs.writeFile(path.join(directory,'owner.json'), JSON.stringify({owner, project}), {mode:0o600});
  return {root, owner, project, directory, image:'quay.io/keycloak/keycloak@sha256:'+'a'.repeat(64),
    ports:{console:45100, idp:45101, controlPlane:45102, controlPlaneB:45103}};
}

async function entries(directory) {
  try { return await fs.readdir(directory); }
  catch (error) { if (error.code === 'ENOENT') return []; throw error; }
}

test('an interrupted credential write before Docker removes all partially created SSO secrets', async t => {
  const {root, directory} = await workspace(t);
  await fs.mkdir(path.join(root,'deploy'));
  for (const name of ['sso-images.lock.json','images.lock.json']) {
    await fs.copyFile(path.join(repository,'deploy',name),path.join(root,'deploy',name));
  }
  const write = fs.writeFile;
  let injected = false;
  t.mock.method(fs,'writeFile',async (...args) => {
    const result = await write(...args);
    if (path.basename(String(args[0])) === 'idp-password') {
      injected = true;
      throw Error('Interrupted private write');
    }
    return result;
  });
  let failure;
  try { await createSsoFixture({root}); } catch (error) { failure=error; }
  assert.equal(injected,true);
  const projects = (await entries(path.join(root,'.runtime'))).filter(name => name !== path.basename(directory));
  const after = (await Promise.all(projects.map(name => entries(path.join(root,'.runtime',name))))).flat();
  assert.deepEqual(after.filter(name => secretFiles.includes(name)),[]);
  assert.equal(failure.cleanup.secretsRemaining,0);
});

test('foreign Docker labels block teardown while local SSO secrets, derived copies and keys are scrubbed', async t => {
  const context = await workspace(t);
  const file = name => path.join(context.directory,name);
  for (const name of secretFiles) await fs.writeFile(file(name),'',{mode:0o600});
  await fs.mkdir(file('keyring'),{mode:0o700});
  await fs.writeFile(file('keyring/key-'+randomUUID()+'.xml'),'');
  await fs.writeFile(file('keep-user-input.txt'),'');
  const external = path.join(context.root,'user-owned');
  await fs.writeFile(external,'');
  await fs.symlink(external,file('keyring/key-'+randomUUID()+'.xml'));
  const commands=[];
  const run=async (binary,args)=>{
    commands.push(args);
    if (args[0]==='ps') return 'foreign-test-container';
    if (args[0]==='inspect') return JSON.stringify({'com.docker.compose.project':context.project,'com.webapi.sso.owner':randomUUID()});
    return '';
  };
  let failure;
  try { await cleanupSsoFixture({...context,run}); } catch (error) { failure=error; }
  assert.deepEqual((await entries(context.directory)).filter(name=>secretFiles.includes(name)),[]);
  assert.deepEqual(await entries(file('keyring')),[]);
  assert.equal((await fs.stat(external)).isFile(),true);
  assert.equal((await fs.stat(file('keep-user-input.txt'))).isFile(),true);
  assert(!commands.some(args=>args.includes('down')||args.includes('rm')));
  assert.equal(failure.cleanup.failed,true);
  assert.equal(failure.cleanup.containersRemaining,1);
  assert.equal(failure.cleanup.secretsRemaining,0);
});

test('a secret name occupied by a directory remains counted and preserves its user input', async t => {
  const context=await workspace(t), file=name=>path.join(context.directory,name);
  await fs.mkdir(file('client-secret'));
  await fs.writeFile(file('client-secret/keep'),'');
  await fs.writeFile(file('login-hmac'),'');
  let failure;
  try { await cleanupSsoFixture({...context,run:async()=>''}); } catch (error) { failure=error; }
  assert.equal((await entries(context.directory)).includes('login-hmac'),false);
  assert.deepEqual(await entries(file('client-secret')),['keep']);
  assert.equal(failure.cleanup.secretsRemaining,1);
});

async function scriptsAndDocker(root, code) {
  await fs.cp(path.join(repository,'scripts'),path.join(root,'scripts'),{recursive:true});
  await fs.mkdir(path.join(root,'bin'));
  await fs.writeFile(path.join(root,'bin/docker'),'#!'+process.execPath+'\n'+code,{mode:0o700});
  return {...process.env,WEBAPI_NODE:process.execPath,PATH:path.join(root,'bin')+':'+process.env.PATH};
}

test('the backend shell refuses foreign Docker ownership and clears its own database password', async t => {
  const {root,directory} = await workspace(t);
  const env=await scriptsAndDocker(root,`
    const fs=require('node:fs'), a=process.argv.slice(2), p=process.env.WEBAPI_SSO_TEST_PROJECT;
    if (a.includes('down')||a.includes('rm')&&!a.includes('--rm')) fs.writeFileSync('unauthorized-removal','');
    if(a[0]==='compose'&&a.includes('ps')||a[0]==='ps') console.log('foreign-container');
    else if(a[0]==='inspect') console.log(JSON.stringify({'com.docker.compose.project':p,'com.webapi.sso.owner':'other'}));
    else if(a[0]==='volume'&&a[1]==='inspect'){console.error('no such volume');process.exitCode=1;}
  `);
  const error=await exec('bash',['scripts/check-sso.sh','domain'],{cwd:root,env}).catch(e=>e);
  assert.equal(error.code,3);
  assert.equal((await entries(root)).includes('unauthorized-removal'),false);
  const projects=(await entries(path.join(root,'.runtime'))).filter(n=>n!==path.basename(directory));
  assert.equal(projects.length,1);
  assert.equal((await entries(path.join(root,'.runtime',projects[0]))).includes('postgres-password'),false);
  const receipt=JSON.parse(error.stdout.trim().split('\n').at(-1));
  assert.equal(receipt.secretsRemaining,0);
  assert.equal(receipt.containersRemaining,1);
});

test('SSO removes generated keyring files while preserving review notes and similarly named user files',async t=>{
  const context=await workspace(t),ring=path.join(context.directory,'keyring');
  await fs.mkdir(ring,{mode:0o700});
  const key=randomUUID(),tmp=randomUUID();
  for (const name of ['key-'+key+'.xml',tmp+'.tmp','review.xml','notes.tmp','key-example.xml']) {
    await fs.writeFile(path.join(ring,name),'');
  }
  const receipt=await cleanupSsoFixture({...context,run:async()=>''});
  assert.equal(receipt.secretsRemaining,0);
  assert.deepEqual((await entries(ring)).sort(),['key-example.xml','notes.tmp','review.xml']);
});

test('a review pointer cannot authorize a replaced directory containing a copied owner marker',async t=>{
  const {publishReviewPointer,ownedReview}=await import('../../scripts/sso/review.mjs');
  const {createSsoDirectory}=await import('../../scripts/sso/owned-run.mjs');
  const context=await workspace(t);
  await fs.unlink(path.join(context.directory,'owner.json'));
  await fs.rmdir(context.directory);
  await createSsoDirectory(context);
  context.endpoints={console:'http://127.0.0.1:45100'};
  await publishReviewPointer(context);
  const moved=path.join(context.root,'original');
  await fs.rename(context.directory,moved);
  await fs.mkdir(context.directory,{mode:0o700});
  await fs.copyFile(path.join(moved,'owner.json'),path.join(context.directory,'owner.json'));
  await fs.writeFile(path.join(context.directory,'postgres-password'),'');
  let failure;
  try { await cleanupSsoFixture({...context,run:async()=>''}); } catch(error){ failure=error; }
  assert.equal(failure?.cleanup?.secretsRemaining,null);
  await assert.rejects(ownedReview(context.root));
  assert((await entries(context.directory)).includes('postgres-password'));
});

test('failed retention cannot overwrite an existing review pointer',async t=>{
  const {publishReviewPointer}=await import('../../scripts/sso/review.mjs');
  const context=await workspace(t); context.endpoints={console:'http://127.0.0.1:45100'};
  const pointer=path.join(context.root,'.runtime','sso-review.json');
  await fs.writeFile(pointer,JSON.stringify({user:'previous review'}),{mode:0o600});
  await assert.rejects(publishReviewPointer(context),{code:'EEXIST'});
  assert.deepEqual(JSON.parse(await fs.readFile(pointer,'utf8')),{user:'previous review'});
});

test('the backend SIGTERM waits for the Docker child, scrubs its credential, and preserves exit 143',async t=>{
  const {root,directory}=await workspace(t);
  const env=await scriptsAndDocker(root,`
    const a=process.argv.slice(2);
    if (a[0]==='volume'&&a[1]==='inspect'){console.error('no such volume');process.exitCode=1;}
    if (a[0]==='compose'&&a.includes('up')){
      process.on('SIGTERM',()=>setTimeout(()=>process.exit(0),30));
      process.kill(process.ppid,'SIGTERM');setTimeout(()=>process.exit(1),8000);
    }
  `);
  const error=await exec('bash',['scripts/check-sso.sh','domain'],{cwd:root,env,timeout:15000}).catch(e=>e);
  assert.equal(error.code,143);
  const receipt=JSON.parse(error.stdout.trim().split('\n').at(-1));
  assert.equal(receipt.secretsRemaining,0);
  assert.equal(receipt.containersRemaining,0);
  assert.deepEqual(await entries(path.join(root,'.runtime')),[path.basename(directory)]);
});

test('an inaccessible Docker inventory leaves unknown resource counts and scrubs verified local files',async t=>{
  const context=await workspace(t);
  await fs.writeFile(path.join(context.directory,'realm.json'),'');
  let failure;
  try {await cleanupSsoFixture({...context,run:async()=>{throw Error('unavailable');}});} catch(error){failure=error;}
  assert.equal(failure.cleanup.containersRemaining,null);
  assert.equal(failure.cleanup.networksRemaining,null);
  assert.equal(failure.cleanup.secretsRemaining,0);
  assert(!(await entries(context.directory)).includes('realm.json'));
});

test('a retained context pointing at another run rejects Docker cleanup and preserves the foreign inputs',async t=>{
  const context=await workspace(t);
  const env=await scriptsAndDocker(context.root,`
    const fs=require('node:fs');
    fs.writeFileSync('unexpected-docker','');
  `);
  const ev=path.join(context.root,'docs/evidence/sso');
  await fs.mkdir(ev,{recursive:true});
  const other=path.join(context.root,'other-project');
  await fs.mkdir(other);
  await fs.writeFile(path.join(other,'client-secret'),'');
  await fs.writeFile(path.join(context.directory,'login-hmac'),'');
  await fs.writeFile(path.join(context.directory,'review-context.json'),JSON.stringify({...context,directory:other}),{mode:0o600});
  const pointer=path.join(context.root,'.runtime','sso-review.json');
  await fs.writeFile(pointer,JSON.stringify({owner:context.owner,project:context.project,directory:context.directory}),{mode:0o600});
  const error=await exec(process.execPath,['scripts/sso/cleanup.mjs'],{cwd:context.root,env}).catch(e=>e);
  assert.equal(error.code,3);
  assert((await entries(other)).includes('client-secret'));
  assert(!(await entries(context.directory)).includes('login-hmac'));
  assert(!(await entries(context.root)).includes('unexpected-docker'));
  const receipt=JSON.parse(await fs.readFile(path.join(ev,'cleanup.json'),'utf8'));
  assert.equal(receipt.failed,true); assert.equal(receipt.secretsRemaining,0);
  assert.equal(receipt.containersRemaining,null);
});

test('an exclusively created credential never replaces an existing file with the same name',async t=>{
  const {root,directory} = await workspace(t);
  await fs.mkdir(path.join(root,'deploy'));
  for(const name of ['sso-images.lock.json','images.lock.json']) await fs.copyFile(path.join(repository,'deploy',name),path.join(root,'deploy',name));
  const write=fs.writeFile;
  let injected=false;
  t.mock.method(fs,'writeFile',async (file,...args)=>{
    if (path.basename(String(file))==='idp-password') {
      injected=true; await write(file,'',{mode:0o600,flag:'wx'});
    }
    return write(file,...args);
  });
  let failure;
  try {await createSsoFixture({root});} catch(error){failure=error;}
  assert(injected);
  const own=(await entries(path.join(root,'.runtime'))).filter(name=>name!==path.basename(directory));
  assert.equal(own.length,1);
  const names=await entries(path.join(root,'.runtime',own[0]));
  assert(names.includes('idp-password'));
  assert(!names.includes('admin-password'));
  assert.equal(failure.cleanup.secretsRemaining,1);
});

test('missing trusted review identity gives unknown counts without contacting Docker',async t=>{
  const context=await workspace(t);
  const env=await scriptsAndDocker(context.root,"require('fs').writeFileSync('unexpected-docker','')");
  await fs.mkdir(path.join(context.root,'docs/evidence/sso'),{recursive:true});
  const pointer=path.join(context.root,'.runtime','sso-review.json');
  await fs.symlink(path.join(context.directory,'owner.json'),pointer);
  await fs.writeFile(path.join(context.directory,'client-secret'),'');
  const error=await exec(process.execPath,['scripts/sso/cleanup.mjs'],{cwd:context.root,env}).catch(e=>e);
  assert.equal(error.code,3);
  assert((await fs.lstat(pointer)).isSymbolicLink());
  assert((await entries(context.directory)).includes('client-secret'));
  assert(!(await entries(context.root)).includes('unexpected-docker'));
  const receipt=JSON.parse(error.stdout.trim());
  assert.equal(receipt.failed,true);
  assert.equal(receipt.secretsRemaining,null);
  assert.equal(receipt.volumesRemaining,null);
});

test('an older default network is removed only when its attached containers prove its owner',async t=>{
  const ctx=await workspace(t);
  const net=ctx.project+'_default',container='2'.repeat(64),networkId='3'.repeat(64);
  let haveContainer=true,haveNetwork=true;
  const run=async(_,args)=>{
    if(args[0]==='ps') return haveContainer?container:'';
    if(args[0]==='inspect') return JSON.stringify({'com.webapi.sso.owner':ctx.owner,'com.docker.compose.project':ctx.project});
    if(args[0]==='volume')return '';
    if(args[0]==='rm'){haveContainer=false;return '';}
    if(args[0]==='network'&&args[1]==='ls')return haveNetwork?net:'';
    if(args[0]==='network'&&args[1]==='inspect'){
      if(args.includes('{{json .Labels}}'))return JSON.stringify({'com.docker.compose.project':ctx.project,'com.docker.compose.network':'default'});
      if(args.includes('{{.Id}}'))return networkId;
      if(args.includes('{{json .Containers}}'))return JSON.stringify(haveContainer?{[container]:{}}:{});
    }
    if(args[0]==='network'&&args[1]==='rm'&&args.at(-1)===networkId){haveNetwork=false;return '';}
    throw Error('Unexpected command in old-fixture cleanup');
  };
  const receipt=await cleanupSsoFixture({...ctx,run});
  assert.equal(receipt.containersRemaining,0);assert.equal(receipt.networksRemaining,0);
  assert.equal(haveNetwork,false);
});

test('an old network without an independently verified attachment is preserved',async t=>{
  const ctx=await workspace(t);
  let mutations=0;
  const run=async(_,args)=>{
    if(args.includes('rm'))mutations++;
    if(args[0]==='network'&&args[1]==='ls')return ctx.project+'_default';
    if(args[0]==='network'&&args[1]==='inspect'){
      if(args.includes('{{json .Labels}}'))return JSON.stringify({'com.docker.compose.project':ctx.project,'com.docker.compose.network':'default'});
      if(args.includes('{{.Id}}'))return '3'.repeat(64);
      if(args.includes('{{json .Containers}}'))return '{}';
    }
    return '';
  };
  let failure;
  try{await cleanupSsoFixture({...ctx,run});}catch(error){failure=error;}
  assert.equal(mutations,0);
  assert.equal(failure.cleanup.networksRemaining,1);
  assert.equal(failure.cleanup.secretsRemaining,0);
});
