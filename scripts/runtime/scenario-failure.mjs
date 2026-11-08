import {createHash} from 'node:crypto';
function dataValue(value,key){try{const d=value&&Object.getOwnPropertyDescriptor(value,key);return d&&'value'in d?d.value:undefined;}catch{return undefined;}}
export function scenarioDataValue(value,key){return dataValue(value,key);}
// Never invoke getters or stringify an unbounded/cyclic diagnostic object.
export function boundedScenarioDiagnostics(input){
 let truncated=false,nodes=0,remaining=24000;const seen=new WeakSet();
 function visit(value,depth){
  if(++nodes>256||depth>5||remaining<=0){truncated=true;return '[truncated]';}
  if(value===null||typeof value==='boolean')return value;
  if(typeof value==='number')return Number.isFinite(value)?value:null;
  if(typeof value==='string'){const bytes=Buffer.from(value.slice(0,2048));const limit=Math.min(2048,remaining);if(value.length>2048||bytes.length>limit)truncated=true;remaining-=Math.min(bytes.length,limit);return bytes.subarray(0,limit).toString('utf8');}
  if(typeof value!=='object'){truncated=true;return '[unsupported]';}
  if(seen.has(value)){truncated=true;return '[circular]';}seen.add(value);
  const output=Array.isArray(value)?[]:Object.create(null);let count=0;
  try{for(const key in value){if(!Object.hasOwn(value,key))continue;if(++count>32||remaining<=0){truncated=true;break;}const d=Object.getOwnPropertyDescriptor(value,key);if(!d||!('value'in d)){truncated=true;continue;}const safeKey=String(key).slice(0,128);remaining-=Buffer.byteLength(safeKey);if(safeKey!==key||safeKey==='__proto__'){truncated=true;continue;}if(/password|secret|token|authorization|credential|api.?key|cookie|connection.?string/i.test(safeKey)){output[safeKey]='[redacted]';truncated=true;}else output[safeKey]=visit(d.value,depth+1);}}
  catch{truncated=true;}return output;
 }
 const value=visit(input,0);if(value&&typeof value==='object'&&!Array.isArray(value))value.diagnosticTruncated=truncated||dataValue(input,'diagnosticTruncated')===true;
 return value;
}
export function boundedScenarioResult(source){const result=dataValue(source,'result');return result&&typeof result==='object'&&!Array.isArray(result)?boundedScenarioDiagnostics(result):null;}
function copyDiagnosticFields(target,source){
 if(!source||typeof source!=='object')return target;
 try{
  const fields=Object.getOwnPropertyDescriptors(source),status=fields.status?.value,result=fields.result?.value;
  if(Number.isSafeInteger(status)||typeof status==='string'&&status.length<=128)target.status=status;
  if(result&&typeof result==='object'&&!Array.isArray(result))target.result=boundedScenarioDiagnostics(result);
 }catch{}
 return target;
}
// JavaScript permits rejecting with any value. Normalize it without losing the
// original reason, so cleanup decisions never depend on its truthiness.
export function scenarioFailure(reason,proof){
 const plainMessage=dataValue(reason,'message');
 const error=reason instanceof Error&&Object.isExtensible(reason)?reason:copyDiagnosticFields(new Error(reason instanceof Error?scenarioFailureMessage(reason):typeof plainMessage==='string'&&plainMessage?plainMessage.slice(0,2048):'Scenario failed with a non-standard rejection.',{cause:reason}),reason);
 try{if(Object.hasOwn(error,'proof')&&error.proof===proof)return error;}catch{}
 return attachScenarioValue(error,'proof',proof);
}
export function scenarioFailureMessage(error){
 const message=dataValue(error,'message');return typeof message==='string'&&message?message.slice(0,2048):'Scenario failed; inspect retained evidence.';
}
export function scenarioFailureStatus(error){const status=dataValue(error,'status');return Number.isSafeInteger(status)?status:typeof status==='string'?status.slice(0,128):null;}
export function scenarioFailureName(error){let current=error;for(let i=0;i<4&&current;i++){const name=dataValue(current,'name');if(typeof name==='string'&&name)return name.slice(0,100);try{current=Object.getPrototypeOf(current);}catch{break;}}return 'Error';}
export function attachScenarioValue(primary,key,value){
 try{if(Object.hasOwn(primary,key)&&primary[key]===value)return primary;}catch{}
 try{if(Object.isExtensible(primary)&&!Object.hasOwn(primary,key)){Object.defineProperty(primary,key,{value,writable:true,configurable:true,enumerable:true});return primary;}}catch{}
 const wrapped=copyDiagnosticFields(new AggregateError([primary],scenarioFailureMessage(primary),{cause:primary}),primary);
 for(const name of ['proof','cleanupError','proofWriteError','secondaryFailures','evidenceDirectory','retainedDirectory'])if(name!==key)try{if(Object.hasOwn(primary,name))wrapped[name]=primary[name];}catch{}
 wrapped[key]=value;return wrapped;
}
export function attachScenarioFailure(primary,key,secondary){
 const error=secondary instanceof Error?secondary:new Error('Secondary scenario operation failed.',{cause:secondary});
 return attachScenarioValue(primary,key,error);
}
export function collectScenarioFailure(items,stage,reason){
 if(items.length>=8)throw new Error('Too many secondary scenario failures');
 items.push({stage,error:reason instanceof Error?reason:new Error('Secondary scenario operation failed.',{cause:reason})});
}
export function describeScenarioFailures(items){return items.map(({stage,error})=>({stage,name:scenarioFailureName(error),messageSha256:createHash('sha256').update(scenarioFailureMessage(error)).digest('hex')}));}
export async function withScenarioHandle(handle,operation){
 let failed=false,reason,result;try{result=await operation(handle);}catch(e){failed=true;reason=e;}
 try{await handle.close();}catch(e){if(failed){const primary=reason instanceof Error?reason:new Error('Scenario I/O operation failed.',{cause:reason});throw attachScenarioFailure(primary,'closeError',e);}throw e;}
 if(failed)throw reason;return result;
}
