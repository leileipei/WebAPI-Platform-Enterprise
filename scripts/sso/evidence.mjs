import fs from 'node:fs/promises';import path from 'node:path';import assert from 'node:assert/strict';import {createHash} from 'node:crypto';
export const sha256=bytes=>createHash('sha256').update(bytes).digest('hex');
export function assertOwnedSsoResources(context,labels){
 assert.match(context.owner,/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/,'Invalid owner');
 assert.equal(context.project,'webapi-sso-test-'+context.owner,'Invalid project owner');
 for(const label of labels){assert.equal(label['com.docker.compose.project'],context.project,'Foreign project');assert.equal(label['com.webapi.sso.owner'],context.owner,'Foreign owner');}
 return true;
}
export async function validateSsoDirectory(context){
 assertOwnedSsoResources(context,[]);const stat=await fs.lstat(context.directory);
 assert.ok(!stat.isSymbolicLink()&&stat.isDirectory(),'Fixture directory cannot be a symlink');
 assert.equal(stat.uid,process.getuid?.(),'Foreign directory owner');assert.equal(stat.mode&0o077,0,'Fixture directory must be private');
 assert.equal(path.basename(context.directory),context.project,'Foreign directory project');
 const ownerPath=path.join(context.directory,'owner.json'),file=await fs.lstat(ownerPath);assert.ok(file.isFile()&&!file.isSymbolicLink(),'Invalid owner file');assert.equal(file.mode&0o077,0,'Owner file must be private');
 const stored=JSON.parse(await fs.readFile(ownerPath,'utf8'));assert.equal(stored.owner,context.owner,'Foreign owner');assert.equal(stored.project,context.project,'Foreign project');return fs.realpath(context.directory);
}
export function assertNoSecrets(value,secrets){const text=typeof value==='string'?value:JSON.stringify(value);for(const secret of secrets)if(secret&&text.includes(secret))throw Error('Secret detected in public evidence');return true;}
export function validatePrivacyReceipt(receipt,fileHashes){assert.equal(receipt.complete,true);assert.equal(receipt.plaintextSecretsFound,0);assert.deepEqual(receipt.files,fileHashes,'Public payload changed after secret scan');return true;}
