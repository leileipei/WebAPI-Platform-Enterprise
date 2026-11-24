import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {isIPv4} from 'node:net';
import {execFileAndWait} from '../runtime/exec-file.mjs';
import {assertOwnedSsoResources, readSsoOwner, ssoDirectoryIdentity} from './evidence.mjs';

const ownerLabel = 'com.webapi.sso.owner';
const projectLabel = 'com.docker.compose.project';
const credentials = ['postgres-password','admin-password','idp-password','idp-admin-password','client-secret',
  'login-hmac','correct-secret','realm.json','fixture-compose.json','operations.log'];

export function ssoNetwork(owner, subnet = process.env.WEBAPI_SSO_TEST_SUBNET) {
  const network = {labels:{[ownerLabel]:owner}};
  if (subnet) {
    const address = subnet.split('/')[0];
    assert(isIPv4(address) && /\.0\/24$/.test(subnet) &&
      /^(?:10\.|192\.168\.|172\.(?:1[6-9]|2[0-9]|3[01])\.)/.test(address), 'Expected a private IPv4 /24 test subnet');
    network.ipam = {config:[{subnet}]};
  }
  return network;
}

export async function createSsoDirectory(context,{keyring=false}={}) {
  assertOwnedSsoResources(context, []);
  const runtime = path.join(context.root,'.runtime');
  assert.equal(context.directory, path.join(runtime,context.project));
  await fs.mkdir(runtime,{recursive:true,mode:0o700});
  assert((await fs.lstat(runtime)).isDirectory(), 'Invalid SSO runtime directory');
  await fs.mkdir(context.directory,{mode:0o700});
  context.directoryIdentity=await ssoDirectoryIdentity(context.directory);
  const marker={owner:context.owner,project:context.project,directoryIdentity:context.directoryIdentity};
  if (keyring) {
    const keyDirectory=path.join(context.directory,'keyring');
    await fs.mkdir(keyDirectory,{mode:0o700});
    marker.keyringIdentity=await ssoDirectoryIdentity(keyDirectory);
  }
  await fs.writeFile(path.join(context.directory,'owner.json'),JSON.stringify(marker),{mode:0o600,flag:'wx'});
}

export async function writeSsoCredential(directory,name,value,mode=0o600) {
  assert(credentials.includes(name));
  try { await fs.writeFile(path.join(directory,name),value,{mode,flag:'wx'}); }
  catch (error) {
    if (error.code==='EEXIST') error.preserveCredentials=[name];
    throw error;
  }
}

async function allowedFiles(directory, identity, owner, project, mode, preserve=[]) {
  const {stdout} = await execFileAndWait(process.execPath,
    [import.meta.filename,mode,identity,owner,project,...preserve],{cwd:directory});
  return JSON.parse(stdout);
}

async function unlinkNames(names,preserve=[]) {
  // Only unlink directory entries. A substituted directory never authorizes recursive deletion.
  await Promise.allSettled(names.filter(name=>!preserve.includes(name)).map(name=>fs.unlink(name)));
  return (await fs.readdir('.')).filter(name => names.includes(name)).length;
}

async function scrubKeyring(expectedIdentity) {
  let stat;
  try { stat = await fs.lstat('keyring'); }
  catch (error) { if (error.code === 'ENOENT') return 0; throw error; }
  if (!stat.isDirectory()) {
    assert.equal(expectedIdentity,undefined,'SSO keyring identity changed');
    return unlinkNames(['keyring']);
  }
  const identity=await ssoDirectoryIdentity('keyring');
  if (expectedIdentity!==undefined) assert.equal(identity,expectedIdentity,'SSO keyring identity changed');
  const {stdout} = await execFileAndWait(process.execPath,
    [import.meta.filename,'--scrub-keys',identity],{cwd:'keyring'});
  assert.equal(await ssoDirectoryIdentity('keyring'),identity,'SSO keyring identity changed');
  const count = JSON.parse(stdout);
  if (count === 0) {
    try { await fs.rmdir('keyring'); } catch (error) { if (error.code !== 'ENOTEMPTY') throw error; }
  }
  return count;
}

async function inventory(context, run, kind) {
  const {project,owner} = context;
  const list = kind==='containers' ? ['ps','-aq','--no-trunc']
    : kind==='volumes' ? ['volume','ls','-q'] : ['network','ls','--format','{{.Name}}'];
  const filters = ['label='+projectLabel+'='+project,'label='+ownerLabel+'='+owner];
  // Explicit names expose collisions even when both expected labels are absent.
  if (kind==='volumes') filters.push('name=^'+project+'_(pg|idp-data|fault-flags|evidence|pg-test|nuget-test)$');
  if (kind==='networks') filters.push('name=^'+project+'_default$');
  const values = new Set();
  for (const filter of filters) {
    for (const value of (await run('docker',[...list,'--filter',filter])).split('\n').filter(Boolean)) values.add(value);
  }
  return values;
}

async function checkResource(context,run,kind,id,legacyNetwork,previousNetworkId) {
  const args = kind==='containers' ? ['inspect','--format','{{json .Config.Labels}}']
    : [kind==='volumes'?'volume':'network','inspect','--format','{{json .Labels}}'];
  const labels = JSON.parse(await run('docker',[...args,id]));
  if (kind==='volumes') {
    assert(id.startsWith(context.project+'_'),'Foreign SSO volume name');
    // Older fixtures pre-created this exact evidence volume with the run owner alone.
    if (id === context.project+'_evidence' && labels?.[ownerLabel] === context.owner && labels[projectLabel] === undefined) return;
  }
  if (kind==='networks') {
    assert.equal(id,context.project+'_default','Foreign SSO network name');
    const inspect=field=>run('docker',['network','inspect','--format',field,id]);
    const networkId=await inspect('{{.Id}}');
    assert.match(networkId,/^[0-9a-f]{64}$/);
    if (previousNetworkId) assert.equal(networkId,previousNetworkId,'SSO network changed');
    if (legacyNetwork && labels?.[ownerLabel]===undefined) {
      assert.equal(labels?.[projectLabel],context.project);
      assert.equal(labels['com.docker.compose.network'],'default');
      // Previous Compose files omitted an owner label on the network. Its exact name
      // and project must be corroborated by the owner labels of every attached container.
      const attached=Object.keys(JSON.parse(await inspect('{{json .Containers}}')));
      assert(attached.length || previousNetworkId,'Unproven legacy SSO network');
      for (const container of attached) {
        assert.match(container,/^[0-9a-f]{64}$/);
        await checkResource(context,run,'containers',container);
      }
    } else assertOwnedSsoResources(context,[labels]);
    return networkId;
  }
  assertOwnedSsoResources(context,[labels]);
}

async function teardownDocker(context, run, receipt, legacyNetwork) {
  const kinds = ['containers','volumes','networks'];
  const resources = {}, networkIds = new Map();
  try {
    for (const kind of kinds) {
      resources[kind] = await inventory(context,run,kind);
      for (const id of resources[kind]) {
        const verified=await checkResource(context,run,kind,id,legacyNetwork);
        if (kind==='networks') networkIds.set(id,verified);
      }
    }
    // Delete only individually verified resources. A retained Compose file can contain
    // secrets or refer to external resources and is never a source of cleanup authority.
    for (const kind of kinds) {
      for (const id of resources[kind]) {
        await checkResource(context,run,kind,id,legacyNetwork,networkIds.get(id));
        const remove = kind==='containers' ? ['rm','--force','--volumes'] : [kind==='volumes'?'volume':'network','rm'];
        await run('docker',[...remove,kind==='networks'?networkIds.get(id):id]);
      }
    }
  } catch {
    receipt.error = 'SSO resource ownership inspection or removal failed';
  }
  for (const kind of kinds) {
    try {
      receipt[kind+'Remaining'] = (await inventory(context,run,kind)).size;
      if (receipt[kind+'Remaining']) receipt.error ??= 'SSO Docker resources remain';
    } catch { receipt.error ??= 'SSO Docker resource count unavailable'; }
  }
}

export async function cleanupSsoRun(context, run, {dockerAttempted=true, backend=false, resourceError, preserve=[]}={}) {
  const {directory,owner,project} = context;
  const receipt = {schemaVersion:2,owner,project,containersRemaining:null,volumesRemaining:null,networksRemaining:null,secretsRemaining:null};
  try {
    assert.equal(directory,path.join(context.root,'.runtime',project),'Foreign fixture location');
    assert((await fs.lstat(path.join(context.root,'.runtime'))).isDirectory(),'Invalid SSO runtime parent');
    const marker=await readSsoOwner(context);
    const identity = await ssoDirectoryIdentity(directory);
    if (resourceError) receipt.error=resourceError;
    else if (dockerAttempted) await teardownDocker(context,run,receipt,marker.directoryIdentity===undefined);
    else receipt.resourceCleanup = 'not-started';
    // Pin the working directory in a subprocess before unlinking, including the keyring itself.
    assert.equal(await ssoDirectoryIdentity(directory),identity);
    receipt.secretsRemaining = await allowedFiles(directory,identity,owner,project,backend?'--scrub-backend':'--scrub',preserve);
    assert.equal(await ssoDirectoryIdentity(directory),identity);
    if (receipt.secretsRemaining !== 0) receipt.error ??= 'SSO credentials remain';
    if (!receipt.error) {
      await allowedFiles(directory,identity,owner,project,backend?'--finish-backend':'--finish');
      const remaining = await fs.readdir(directory);
      if (remaining.length === 0 && await ssoDirectoryIdentity(directory) === identity) await fs.rmdir(directory);
    }
  } catch { receipt.error ??= 'SSO workspace ownership or cleanup could not be verified'; }
  if (receipt.error) {
    receipt.failed = true;
    throw Object.assign(new Error(receipt.error),{cleanup:receipt});
  }
  return receipt;
}

if (process.argv[1]===import.meta.filename) {
  const [mode,identity,owner,project,...preserve] = process.argv.slice(2);
  assert.equal(await ssoDirectoryIdentity('.'),identity,'SSO cleanup directory changed');
  if (mode === '--scrub-keys') {
    const guid='[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}';
    const keyName=new RegExp('^(?:(?:key-|revocation-)'+guid+'\\.xml|revocation-[0-9]{8}T[0-9]{13}Z\\.xml|'+guid+'\\.tmp)$','i');
    const names=(await fs.readdir('.')).filter(name=>keyName.test(name));
    console.log(await unlinkNames(names));
  } else {
    assert(['--scrub','--scrub-backend','--finish','--finish-backend'].includes(mode));
    const marker=await readSsoOwner({directory:process.cwd(),owner,project,directoryIdentity:identity});
    if (mode.startsWith('--scrub')) {
      const protectedNames=[...new Set([...(marker.preserveCredentials || []),...preserve])];
      assert(protectedNames.every(name=>credentials.includes(name)),'Unexpected SSO credential name');
      if (preserve.length) {
        marker.preserveCredentials=protectedNames;
        await fs.writeFile('owner.json',JSON.stringify(marker),{mode:0o600});
      }
      let count = await unlinkNames(mode==='--scrub-backend'?['postgres-password']:credentials,protectedNames);
      if (mode==='--scrub') count += await scrubKeyring(marker.keyringIdentity);
      console.log(count);
    } else {
      const names = mode==='--finish-backend' ? ['nuget-seed.yml'] : ['review-context.json','result.json'];
      await unlinkNames(names);
      if (mode==='--finish') {
        for (const name of ['observations','build']) await fs.rm(name,{recursive:true,force:true});
      }
      const entries=await fs.readdir('.');
      if (entries.length===1 && entries[0]==='owner.json') await fs.unlink('owner.json');
      console.log(true);
    }
  }
}
