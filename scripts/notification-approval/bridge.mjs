import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {createHash} from 'node:crypto';
import {pathToFileURL} from 'node:url';
import {validGuid,writePrivate,ownedDirectory} from '../runtime/state.mjs';
import {docker} from '../runtime/docker.mjs';
const exec=promisify(execFile),sha=b=>createHash('sha256').update(b).digest('hex');
const approvedBaseline='c1c271555e3a125d22184697c17320c94a4db07c';
export const bridgeChangedFiles=new Set(['src/WebApi.Infrastructure/Settings/SettingsValues.cs','src/WebApi.Infrastructure/Settings/SystemSettingsReader.cs','src/WebApi.Infrastructure/Settings/SystemSettingsService.cs','src/WebApi.Infrastructure/Alerts/AlertRuleService.cs','src/WebApi.Infrastructure/Notifications/CompatibilityNotificationMode.cs','src/WebApi.ControlPlane/ControlPlaneApp.cs','tests/WebApi.Integration.Tests/NotificationCompatibilityBridgeTests.cs']);
export function validateBridgeReceipt(receipt,expected){try{assert(receipt?.schemaVersion===1);assert(/^[a-f0-9]{40}$/.test(receipt.baselineRevision)&&receipt.baselineRevision===expected.baselineRevision);assert(/^[a-f0-9]{40}$/.test(receipt.bridgeRevision)&&receipt.bridgeRevision===expected.bridgeRevision&&receipt.bridgeRevision!==receipt.baselineRevision);assert(/^sha256:[a-f0-9]{64}$/.test(receipt.imageId)&&receipt.imageId===expected.bridgeImageId&&receipt.imageLabel===receipt.bridgeRevision);assert(/^[a-f0-9]{64}$/.test(receipt.patchSha256)&&receipt.patchSha256===expected.patchSha256);assert(Object.keys(expected.lockFiles??{}).length>=2);assert.deepEqual(receipt.lockFiles,expected.lockFiles);assert(Object.values(receipt.lockFiles).every(v=>/^[a-f0-9]{64}$/.test(v)));assert(Array.isArray(receipt.changedFiles)&&receipt.changedFiles.length&&new Set(receipt.changedFiles).size===receipt.changedFiles.length&&receipt.changedFiles.every(f=>bridgeChangedFiles.has(f)));assert(receipt.readonlyMode==='ReadOnly'&&receipt.notificationsWritable===false&&receipt.rulesWritable===false);assert(receipt.buildSource==='git-archive'&&receipt.dirtyCheckoutIncluded===false);return {passed:true,errors:[]};}catch(error){return{passed:false,errors:[error instanceof Error?error.message:'Invalid bridge receipt']};}}
// The old builder is loaded from its immutable Git archive, never from candidate
// sources or a mutable bridge checkout. Its seven applications remain separate.
export async function buildNotificationBridge({baselineRevision,directory,owner,bridgeRevision,root,nodePath=process.execPath,nugetSeedVolume='webapi-enterprise-core-test_nuget-test'}){
 assert.equal(baselineRevision,approvedBaseline);assert(validGuid(owner));
 assert(/^[a-f0-9]{40}$/.test(bridgeRevision)&&bridgeRevision!==baselineRevision);
 directory=path.resolve(directory);
 assert(directory.split(path.sep).includes('.runtime')&&path.basename(directory)!=='local','Bridge builds require separate private build storage');
 assert(!(await fs.lstat(path.join(directory,'runtime.json')).catch(error=>{if(error.code==='ENOENT')return null;throw error;})),'Existing runtime cannot be a bridge build destination');
 root=await fs.realpath(root);
 const git=async args=>(await exec('git',args,{cwd:root,maxBuffer:16*1024*1024})).stdout;
 assert.equal((await git(['rev-parse',bridgeRevision+'^{commit}'])).trim(),bridgeRevision);
 await git(['merge-base','--is-ancestor',baselineRevision,bridgeRevision]);
 const changedFiles=(await git(['diff','--name-only',baselineRevision,bridgeRevision])).trim().split('\n').filter(Boolean);
 assert(changedFiles.length&&changedFiles.every(name=>bridgeChangedFiles.has(name)),'Unapproved bridge patch');
 const lockNames=(await git(['ls-tree','-r','--name-only',baselineRevision])).trim().split('\n').filter(name=>name.endsWith('/packages.lock.json')||['Directory.Packages.props','console/package.json','console/pnpm-lock.yaml','deploy/images.lock.json'].includes(name));
 const lockFiles={};for(const name of lockNames){const before=await git(['show',baselineRevision+':'+name]),after=await git(['show',bridgeRevision+':'+name]);assert.equal(after,before,'Bridge dependency change: '+name);lockFiles[name]=sha(after);}
 const patch=await git(['diff','--binary',baselineRevision,bridgeRevision]);
 await fs.mkdir(directory,{mode:0o700,recursive:true});await ownedDirectory(directory);
 const ownership={schemaVersion:1,ownerId:owner,baselineRevision,bridgeRevision};
 const marker=path.join(directory,'bridge-owner.json');try{assert.deepEqual(JSON.parse(await fs.readFile(marker)),ownership);}catch(error){if(error.code!=='ENOENT')throw error;await writePrivate(marker,ownership);}
 const snapshot=path.join(directory,'builder-source');await fs.mkdir(snapshot,{mode:0o700});
 const archive=path.join(directory,'builder-source.tar');await exec('git',['archive','--output',archive,bridgeRevision],{cwd:root});await exec('tar',['-xf',archive,'-C',snapshot]);
 const {buildRelease}=await import(pathToFileURL(path.join(snapshot,'scripts/runtime/build.mjs')));
 const release=await buildRelease({revision:bridgeRevision,directory,root,nodePath,nugetSeedVolume});
 const image=JSON.parse((await docker(['image','inspect',release.imageId])).stdout)[0];
 const receipt={schemaVersion:1,baselineRevision,bridgeRevision,imageId:release.imageId,imageLabel:image.Config.Labels['org.opencontainers.image.revision'],patchSha256:sha(patch),lockFiles,changedFiles,readonlyMode:'ReadOnly',notificationsWritable:false,rulesWritable:false,buildSource:'git-archive',dirtyCheckoutIncluded:false,ownerId:owner};
 const gate=validateBridgeReceipt(receipt,{baselineRevision,bridgeRevision,bridgeImageId:release.imageId,patchSha256:sha(patch),lockFiles});assert(gate.passed,gate.errors.join('; '));
 await writePrivate(path.join(directory,'bridge-patch.diff'),patch);await writePrivate(path.join(directory,'bridge-receipt.json'),receipt);return receipt;
}
