import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import {ownedDirectory} from './state.mjs';

const keys=['maxSnapshotBytes','minHostFreeBytes','maxTargetBytes','minTargetFreeBytes'];
const issuedProofs=new WeakMap();
const encodeProof=proof=>JSON.stringify(proof)+'\n';
const bindingOf=(budget,provider,projectName,directory,volumeNames,phase,ownerId,volumeIdentities)=>({budget:JSON.stringify(budget),provider,projectName,directory,volumeNames:JSON.stringify([...volumeNames].sort()),phase,ownerId:ownerId??null,volumeIdentities:volumeIdentities?JSON.stringify(Object.entries(volumeIdentities).sort()):null});

// No production default: the operator/integration must supply explicit limits.
export function validateRestoreStorageBudget(value){
 if(!value||typeof value!=='object'||Array.isArray(value)||Object.keys(value).sort().join(',')!==[...keys].sort().join(',')||
    keys.some(key=>!Number.isSafeInteger(value[key])||value[key]<1)||
    value.maxSnapshotBytes+value.maxTargetBytes+value.minHostFreeBytes>Number.MAX_SAFE_INTEGER||
    value.maxTargetBytes+value.minTargetFreeBytes>Number.MAX_SAFE_INTEGER)throw new Error('Explicit restore storage budget is required');
 return Object.freeze(Object.fromEntries(keys.map(key=>[key,value[key]])));
}
// Callers which will restore must fail before expensive setup if the
// integration is unavailable. This does not attest the provider itself.
export function requireRestoreIntegration({restoreBudget,storageVerifier}={}){
 const budget=validateRestoreStorageBudget(restoreBudget);
 if(typeof storageVerifier!=='function')throw new Error('Restore target hard quota integration is unavailable');
 return budget;
}

// Only a proof returned by this process can authorize the next stage. This is
// an accidental-misuse guard, not protection from the same host user.
export function requireBoundRestoreProof(proof,{budget,provider,projectName,directory,volumeNames,phase,ownerId,volumeIdentities}){
 const expected=bindingOf(validateRestoreStorageBudget(budget),provider,projectName,directory,volumeNames,phase,ownerId,volumeIdentities),actual=issuedProofs.get(proof);
 if(!actual||Object.keys(expected).some(key=>actual[key]!==expected[key]))throw new Error('Bound restore preflight proof is required');
 return proof;
}

// The provider is an integration boundary: it must verify a real enforced
// quota and target-volume identity. A locally fabricated object is not proof.
export async function requireRestoreStorageProof({provider,budget,phase,projectName,directory,volumeNames,ownerId,volumeIdentities,previous}){
 budget=validateRestoreStorageBudget(budget);
 if(typeof provider!=='function')throw new Error('Restore target hard quota is unverified');
 if(!['preflight','before-initialization','before-extraction'].includes(phase)||!Array.isArray(volumeNames)||new Set(volumeNames).size!==volumeNames.length)throw new Error('Invalid restore storage scope');
 const hasIdentities=phase!=='preflight';
 if(hasIdentities&&(!/^[a-f0-9-]{36}$/.test(ownerId??'')||!volumeIdentities||typeof volumeIdentities!=='object'||
    JSON.stringify(Object.keys(volumeIdentities).sort())!==JSON.stringify([...volumeNames].sort())||Object.values(volumeIdentities).some(value=>!/^[a-f0-9]{64}$/.test(value))))throw new Error('Restore target volume identities are unverified');
 if(hasIdentities)requireBoundRestoreProof(previous,{budget,provider,projectName,directory,volumeNames,phase:phase==='before-initialization'?'preflight':'before-initialization',...(phase==='before-extraction'?{ownerId,volumeIdentities}:{})});
 const proof=await provider({phase,projectName,directory,volumeNames:[...volumeNames].sort(),budget,...(hasIdentities?{ownerId,volumeIdentities}:{})});
 const expected=['schemaVersion','kind','phase','projectName','directory','quotaId','quotaBytes','availableBytes','evidenceDigest','volumeNames',...(hasIdentities?['ownerId','volumeIdentities']:[])].sort();
 if(!proof||typeof proof!=='object'||Array.isArray(proof)||JSON.stringify(Object.keys(proof).sort())!==JSON.stringify(expected)||proof.schemaVersion!==1||proof.kind!=='enforced-hard-quota'||proof.phase!==phase||
    proof.projectName!==projectName||proof.directory!==directory||!/^[-a-zA-Z0-9_]{16,128}$/.test(proof.quotaId??'')||
    !Number.isSafeInteger(proof.quotaBytes)||proof.quotaBytes<1||proof.quotaBytes>budget.maxTargetBytes||
    !Number.isSafeInteger(proof.availableBytes)||proof.availableBytes<proof.quotaBytes+budget.minTargetFreeBytes||
    !/^[a-f0-9]{64}$/.test(proof.evidenceDigest??'')||!Array.isArray(proof.volumeNames)||
    JSON.stringify([...proof.volumeNames].sort())!==JSON.stringify([...volumeNames].sort())||
    hasIdentities&&(proof.ownerId!==ownerId||JSON.stringify(Object.entries(proof.volumeIdentities??{}).sort())!==JSON.stringify(Object.entries(volumeIdentities).sort()))||
    previous&&proof.quotaId!==previous.quotaId)throw new Error('Restore target hard quota is unverified');
 const providerIdentity=Object.freeze({kind:'javascript-source-fingerprint',sourceSha256:createHash('sha256').update(Function.prototype.toString.call(provider)).digest('hex')});
 const issued=Object.freeze({...proof,volumeNames:Object.freeze([...proof.volumeNames]),...(hasIdentities?{volumeIdentities:Object.freeze({...proof.volumeIdentities})}:{}),
  traceFormatVersion:2,budget,providerIdentity,operationId:previous?.operationId??randomUUID(),previousProofSha256:previous?createHash('sha256').update(encodeProof(previous)).digest('hex'):null});
 issuedProofs.set(issued,bindingOf(budget,provider,projectName,directory,volumeNames,phase,hasIdentities?ownerId:undefined,hasIdentities?volumeIdentities:undefined));return issued;
}
export async function persistRestoreStorageProof(directory,proof){
 directory=await ownedDirectory(directory);
 if(!proof||!['preflight','before-initialization','before-extraction'].includes(proof.phase))throw new Error('Invalid restore storage proof');
 const file=path.join(directory,'.webapi-restore-quota-'+proof.phase+'.json');
 const handle=await fs.open(file,'wx',0o600);
 try{await handle.writeFile(encodeProof(proof));await handle.sync();}finally{await handle.close();}
 const parent=await fs.open(directory,'r');try{await parent.sync();}finally{await parent.close();}
 return file;
}
