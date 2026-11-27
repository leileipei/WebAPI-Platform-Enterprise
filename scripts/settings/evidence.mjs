import fs from 'node:fs/promises';import path from 'node:path';import assert from 'node:assert/strict';import {randomUUID,randomBytes,createHash} from 'node:crypto';
import {cleanupAcceptanceRun} from '../acceptance-credentials.mjs';
import {ownedDirectory,writePrivate} from '../runtime/state.mjs';
export const sha=b=>createHash('sha256').update(b).digest('hex');
export function assertNoSecrets(value,secrets){const text=typeof value==='string'?value:JSON.stringify(value);for(const secret of secrets)if(secret&&text.includes(secret))throw Error('Secret detected in public evidence');return true;}
export const secretNames=['password','postgres-password','node-a','node-b','ip-hmac','cursor-key','login-hmac'];
export const settingsCredentialFiles = [...secretNames, 'original.snapshot', 'rollback.snapshot'];

export async function validateSettingsDirectory(root, workspace) {
  validateOwnedResources(workspace, []);
  const runtime = path.join(path.resolve(root), '.runtime');
  assert.equal(workspace.directory, path.join(runtime, workspace.project), 'Owned directory mismatch');
  assert((await fs.lstat(runtime)).isDirectory(), 'Runtime parent must be a directory');
  await ownedDirectory(workspace.directory);
}

export async function createPrivateWorkspace(root) {
  const owner = randomUUID(), project = 'webapi-enterprise-e2e-settings-' + owner;
  const runtime = path.join(path.resolve(root), '.runtime'), directory = path.join(runtime, project);
  await fs.mkdir(runtime, {recursive:true, mode:0o700});
  assert((await fs.lstat(runtime)).isDirectory(), 'Runtime parent must be a directory');
  await fs.mkdir(directory, {mode:0o700});
  const workspace = {owner, project, directory};
  await validateSettingsDirectory(root, workspace);
  try {
    await writePrivate(path.join(directory, 'owner.json'), {owner, project});
    await writePrivate(path.join(directory, 'review-context.json'), {...workspace, args:null, composeStarted:false});
    for (const name of secretNames) {
      const encoding = ['ip-hmac','cursor-key','login-hmac'].includes(name) ? 'base64' : 'base64url';
      await fs.writeFile(path.join(directory, name), randomBytes(32).toString(encoding), {mode:0o600, flag:'wx'});
    }
  } catch (error) {
    error.settingsWorkspace = workspace;
    error.settingsCleanup = await cleanupAcceptanceRun(directory, settingsCredentialFiles, async () => ({}));
    throw error;
  }
  return workspace;
}
export function validateOwnedResources(context,labels){assert.match(context.project,/^webapi-enterprise-e2e-settings-[0-9a-f-]{36}$/);assert.equal(context.project,'webapi-enterprise-e2e-settings-'+context.owner);for(const label of labels){assert.equal(label['com.docker.compose.project'],context.project);assert.equal(label['com.webapi.settings.owner'],context.owner);}return true;}
export async function toolManifest(root){
 const files={};
 const excluded=new Set(['.git','.runtime','.secrets','.worktrees','.superpowers','node_modules','bin','obj','dist','__pycache__']);
 const logger=path.join(root,'scripts/contracts/runner');
 const loggerInputs=new Set(['ContractCompletionLogger.cs','WebApi.Contracts.TestLogger.csproj','packages.lock.json']);
 async function visit(dir){
  for(const entry of (await fs.readdir(dir,{withFileTypes:true})).sort((a,b)=>a.name.localeCompare(b.name,'en'))){
   if(excluded.has(entry.name))continue;
   const full=path.join(dir,entry.name);
   if(entry.isDirectory())await visit(full);
   else if(entry.isFile() && (/\.(mjs|sh|py)$/.test(entry.name) || dir===logger && loggerInputs.has(entry.name)))
    files[path.relative(root,full)]=sha(await fs.readFile(full));
  }
 }
 await visit(path.join(root,'scripts'));
 return {schemaVersion:1,files,sha256:sha(JSON.stringify(files))};
}
export async function verifyToolManifest(root,manifest){assert.deepEqual(await toolManifest(root),manifest,'Acceptance tool source changed');return true;}
