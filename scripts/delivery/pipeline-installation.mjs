import {isDeepStrictEqual} from 'node:util';
export function verifyPipelineInstallation(before,after,candidate){
 const errors=[],require=(v,m)=>{if(!v)errors.push(m);};
 for(const field of ['protectedRows','reports','privateFiles','oldTools','identity','running','explicitGrants'])require(before?.[field]!==undefined&&isDeepStrictEqual(before[field],after?.[field]),'Protected '+field+' changed');
 require(/^[a-f0-9]{40}$/.test(candidate?.sourceRevision)&&after?.sourceRevision===candidate.sourceRevision,'Fixed source mismatch');require(/^sha256:[a-f0-9]{64}$/.test(candidate?.imageId)&&after?.imageId===candidate.imageId,'Fixed image mismatch');
 for(const field of ['applications','staticFiles'])require(Object.keys(candidate?.[field]??{}).length>0&&isDeepStrictEqual(candidate[field],after?.[field]),'Observed '+field+' mismatch');
 require(after?.newDefaultPipelineGrantsOutsideAdmin===0,'Default grant outside Admin');require(isDeepStrictEqual([...(after?.newAdminPipelinePermissions??[])].sort(),['pipeline.manage','pipeline.read','pipeline.run']),'Admin catalog incomplete');require(after?.ready===true,'Runtime not Ready');
 const receipts=candidate?.newReceipts;require(Array.isArray(receipts)&&Array.isArray(after?.newRows),'Exact new receipt inventory missing');if(Array.isArray(receipts)&&Array.isArray(after?.newRows)){require(receipts.every(r=>r.table&&r.id&&r.hash&&r.commandId),'Unbound allowed receipt');require(after.newRows.every(r=>receipts.some(a=>isDeepStrictEqual(a,r)))&&receipts.every(r=>after.newRows.some(a=>isDeepStrictEqual(a,r))),'Unexpected new persisted receipt');}
 return {passed:errors.length===0,errors};
}
