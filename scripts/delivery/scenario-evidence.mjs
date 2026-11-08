import fs from 'node:fs/promises';
import {constants} from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import {scenarioFailure,scenarioFailureName,scenarioDataValue,attachScenarioFailure,attachScenarioValue,withScenarioHandle} from '../runtime/scenario-failure.mjs';

export async function createScenarioEvidenceDirectory(baseDirectory){
 const base=path.resolve(baseDirectory);await fs.mkdir(base,{recursive:true,mode:0o700});
 const output=await fs.mkdtemp(path.join(base,'run-'));await fs.chmod(output,0o700);
 const parent=await fs.open(base,'r');await withScenarioHandle(parent,async h=>h.sync());
 return output;
}

async function syncDirectory(output){const directory=await fs.open(output,'r');await withScenarioHandle(directory,async h=>h.sync());}
async function exclusiveRecord(file,bytes){const handle=await fs.open(file,'wx',0o600);await withScenarioHandle(handle,async h=>{await h.writeFile(bytes);await h.sync();});}
async function pendingExists(output){return !!await fs.lstat(path.join(output,'.proof-pending.json')).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));}
const maxProofBytes=16*1024*1024;
export async function writeScenarioProof(output,proof){
 const file=path.join(output,'proof.json'),marker=path.join(output,'.proof-pending.json'),receipt=path.join(output,'.proof-receipt.json');
 if(!proof||typeof proof!=='object'||Array.isArray(proof)||['evidencePersistence','executionIsolatedAcceptance','executionPhase'].some(key=>Object.hasOwn(proof,key)))throw Error('Invalid scenario proof fields');
 const stored={...proof,evidencePersistence:'pending'};
 if(proof.isolatedAcceptance===true){stored.executionIsolatedAcceptance=true;stored.executionPhase=proof.phase??null;stored.isolatedAcceptance=false;stored.phase='evidence-pending';}
 const bytes=Buffer.from(JSON.stringify(stored,null,2)+'\n');
 if(bytes.length>maxProofBytes)throw Error('Scenario proof exceeds size limit');
 for(const existing of [file,marker,receipt])if(await fs.lstat(existing).catch(error=>error.code==='ENOENT'?null:Promise.reject(error)))throw Object.assign(Error('Scenario proof already exists'),{code:'EEXIST'});
 await exclusiveRecord(marker,JSON.stringify({schemaVersion:1,phase:'pending'})+'\n');await syncDirectory(output);
 await exclusiveRecord(file,bytes);await syncDirectory(output);
 await exclusiveRecord(receipt,JSON.stringify({schemaVersion:1,proofSha256:createHash('sha256').update(bytes).digest('hex')})+'\n');await syncDirectory(output);
 // A lost unlink may resurrect a conservative pending marker after a crash.
 await fs.unlink(marker);return file;
}
async function readStable(file,maxBytes){
 const before=await fs.lstat(file);if(!before.isFile()||before.isSymbolicLink()||before.nlink!==1||before.size>maxBytes)throw Error('Unsafe scenario evidence file');
 const handle=await fs.open(file,constants.O_RDONLY|constants.O_NOFOLLOW);
 return withScenarioHandle(handle,async()=>{const opened=await handle.stat();if(['dev','ino','size','mtimeMs','ctimeMs'].some(key=>opened[key]!==before[key]))throw Error('Scenario evidence changed');
  const bytes=await handle.readFile(),after=await handle.stat(),named=await fs.lstat(file);if(bytes.length>maxBytes||['dev','ino','size','mtimeMs','ctimeMs'].some(key=>opened[key]!==after[key]||opened[key]!==named[key]))throw Error('Scenario evidence changed');return bytes;
 });
}
export async function readScenarioProof(output){
 if(await pendingExists(output))throw Error('Scenario proof persistence is pending');
 const receiptFile=path.join(output,'.proof-receipt.json'),info=await fs.lstat(receiptFile).catch(error=>error.code==='ENOENT'?null:Promise.reject(error));
 if(!info){
  const legacyBytes=await fs.readFile(path.join(output,'proof.json'),'utf8');let legacy;try{legacy=JSON.parse(legacyBytes);}catch(error){throw Error('Invalid scenario proof',{cause:error});}
  if(legacy&&Object.hasOwn(legacy,'evidencePersistence'))throw Error('Scenario proof receipt is missing');
  if(await pendingExists(output))throw Error('Scenario proof persistence is pending');return legacy;
 }
 const bytes=await readStable(path.join(output,'proof.json'),maxProofBytes);let proof;
 try{proof=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes));}catch{throw Error('Invalid scenario proof');}
 if(proof?.evidencePersistence!=='pending')throw Error('Invalid scenario proof persistence state');
 if(proof?.evidencePersistence==='pending'){
  const directory=await fs.lstat(output);if(!directory.isDirectory()||directory.isSymbolicLink()||directory.uid!==process.getuid()||(directory.mode&0o077))throw Error('Unsafe scenario evidence directory');
  if(info.uid!==process.getuid()||(info.mode&0o077))throw Error('Unsafe scenario proof receipt');
  const receiptBytes=await readStable(receiptFile,65536);let receipt;try{receipt=JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(receiptBytes));}catch(error){throw Error('Invalid scenario proof receipt',{cause:error});}
  if(receipt?.schemaVersion!==1||Object.keys(receipt).sort().join(',')!=='proofSha256,schemaVersion'||receipt.proofSha256!==createHash('sha256').update(bytes).digest('hex'))throw Error('Scenario proof receipt mismatch');
  if(proof.executionIsolatedAcceptance===true){if(proof.phase!=='evidence-pending'||typeof proof.executionPhase!=='string')throw Error('Invalid scenario proof completion');proof.isolatedAcceptance=true;proof.phase=proof.executionPhase;}
  proof.evidencePersistence='verified';
 }
 if(await pendingExists(output))throw Error('Scenario proof persistence is pending');return proof;
}

export async function prepareRecordedScenario({baseDirectory,fixtureDirectory,sourceRevision,classification,prepare}){
 const fixture=path.resolve(fixtureDirectory),output=await createScenarioEvidenceDirectory(baseDirectory);
 try{return {output,context:await prepare()};}
 catch(error){
  const fixtureExists=await fs.lstat(fixture).then(()=>true,e=>{if(e.code==='ENOENT')return false;throw e;}).catch(()=>null);
  const raw=scenarioDataValue(error,'message'),rawMessage=typeof raw==='string'?raw:'',message=rawMessage.slice(0,4096),code=scenarioDataValue(error,'code');
  const category=/quota/i.test(message)?'quota':/space|disk/i.test(message)?'storage':/timeout|deadline/i.test(message)?'timeout':/migration/i.test(message)?'migration':'setup';
  const proof={schemaVersion:1,classification,sourceRevision,phase:'setup-failed',fixtureDirectory:fixture,fixtureExists,
   failure:{name:scenarioFailureName(error),code:typeof code==='string'?code.slice(0,100):null,category,
    messageSha256:createHash('sha256').update(message).digest('hex'),messageTruncated:rawMessage.length>message.length,rawLogStatus:'unavailable'},isolatedAcceptance:false};
  let failure=scenarioFailure(error,proof);
  try{await writeScenarioProof(output,proof);}catch(writeError){failure=attachScenarioFailure(failure,'proofWriteError',writeError);}
  failure=attachScenarioValue(failure,'evidenceDirectory',output);failure=attachScenarioValue(failure,'retainedDirectory',fixture);
  throw failure;
 }
}
