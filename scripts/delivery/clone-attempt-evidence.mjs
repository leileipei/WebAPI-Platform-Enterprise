import fs from 'node:fs/promises';
import path from 'node:path';
import {scenarioDataValue} from '../runtime/scenario-failure.mjs';

async function pathKind(file){try{const stat=await fs.lstat(file);return stat.isSymbolicLink()?'symlink':stat.isDirectory()?'directory':'other';}catch(error){return error?.code==='ENOENT'?'missing':'Unknown';}}
export function rememberRestoreResult(attempt,result){
 const complete=scenarioDataValue(result,'restoreComplete'),reference=scenarioDataValue(result,'restoreInputs');
 attempt.restoreOutcome=complete===true?'Completed':complete===false?'PendingReview':'Unknown';
 if(typeof reference==='string'&&path.isAbsolute(reference)&&reference.length<=4096)attempt.retainedRestoreInputs=reference;
}
export async function recordCloneAttempt(attempt,{failed,error}={}){
 attempt.scenarioOutcome=failed?'Failed':'Completed';
 if(!attempt.restoreOutcome||attempt.restoreOutcome==='InProgress')attempt.restoreOutcome='Unknown';
 attempt.targetEntryKind=await pathKind(attempt.cloneDirectory);
 attempt.directoryExists=attempt.targetEntryKind==='Unknown'?null:attempt.targetEntryKind==='directory';
 attempt.identityStatus='Unknown';
 attempt.intentSearchParent=path.dirname(attempt.cloneDirectory);
 let reason=error,reference=attempt.retainedRestoreInputs,failureReference;
 if(typeof reference!=='string'||!path.isAbsolute(reference)||reference.length>4096)reference=undefined;
 for(let i=0;i<4&&reason;i++){const value=scenarioDataValue(reason,'retainedRestoreInputs');if(typeof value==='string'&&path.isAbsolute(value)&&value.length<=4096){failureReference=value;break;}reason=scenarioDataValue(reason,'cause');}
 if(!reference)reference=failureReference;
 else if(failureReference&&failureReference!==reference){attempt.failureRetainedRestoreInputs=failureReference;attempt.failureRestoreInputsKind=await pathKind(failureReference);}
 if(reference){attempt.retainedRestoreInputs=reference;attempt.restoreInputsKind=await pathKind(reference);attempt.restoreInputsExist=attempt.restoreInputsKind==='Unknown'?null:attempt.restoreInputsKind!=='missing';}
 else attempt.restoreInputsExist=null;
 return attempt;
}
