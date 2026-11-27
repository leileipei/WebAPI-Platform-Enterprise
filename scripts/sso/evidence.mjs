import fs from 'node:fs/promises';import {constants} from 'node:fs';import path from 'node:path';import assert from 'node:assert/strict';import {createHash} from 'node:crypto';
export const sha256=bytes=>createHash('sha256').update(bytes).digest('hex');
export function assertOwnedSsoResources(context,labels){
 assert.match(context.owner,/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/,'Invalid owner');
 assert.equal(context.project,'webapi-sso-test-'+context.owner,'Invalid project owner');
 for(const label of labels){assert.equal(label['com.docker.compose.project'],context.project,'Foreign project');assert.equal(label['com.webapi.sso.owner'],context.owner,'Foreign owner');}
 return true;
}
export async function ssoDirectoryIdentity(directory) {
 const st=await fs.lstat(directory,{bigint:true});
 assert(st.isDirectory() && st.uid===BigInt(process.getuid()) && !(st.mode & 0o077n),'Invalid private SSO directory');
 return st.dev+':'+st.ino;
}
export async function readSsoOwner(context) {
 assertOwnedSsoResources(context,[]);
 assert.equal(path.basename(context.directory),context.project,'Foreign directory project');
 const identity=await ssoDirectoryIdentity(context.directory);
 if(context.directoryIdentity!==undefined)assert.equal(identity,context.directoryIdentity,'SSO directory identity changed');
 const handle=await fs.open(path.join(context.directory,'owner.json'),constants.O_RDONLY|constants.O_NOFOLLOW);
 try {
  const st=await handle.stat();
  assert(st.isFile() && st.nlink===1 && st.uid===process.getuid() && !(st.mode & 0o077),'Invalid SSO owner file');
  const marker=JSON.parse(await handle.readFile('utf8'));
  assert.equal(marker.owner,context.owner,'Foreign owner');
  assert.equal(marker.project,context.project,'Foreign project');
  if(marker.directoryIdentity!==undefined)assert.equal(identity,marker.directoryIdentity,'SSO owner directory changed');
  return marker;
 } finally {await handle.close();}
}
export async function validateSsoDirectory(context) {
 await readSsoOwner(context);
 return fs.realpath(context.directory);
}
export function assertNoSecrets(value,secrets){const text=typeof value==='string'?value:JSON.stringify(value);for(const secret of secrets)if(secret&&text.includes(secret))throw Error('Secret detected in public evidence');return true;}
export function validatePrivacyReceipt(receipt,fileHashes){assert.equal(receipt.complete,true);assert.equal(receipt.plaintextSecretsFound,0);assert.deepEqual(receipt.files,fileHashes,'Public payload changed after secret scan');return true;}
