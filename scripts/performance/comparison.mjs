import {createHash} from 'node:crypto';
import {cellKey} from './report.mjs';
import {buildComparisonPairs} from './matrix.mjs';
const sha=/^[a-f0-9]{64}$/;
const topology=cell=>JSON.stringify({scale:cell.scale,auth:cell.auth,concurrency:cell.concurrency,repeat:cell.repeat,nodeMode:cell.nodeMode});
export async function executeComparisonPairs({pairs,switchVariant,measure,onPair}){
  if(!Array.isArray(pairs)||!pairs.length||new Set(pairs.map(p=>p.pairId)).size!==pairs.length||pairs.some(p=>!Array.isArray(p.cells)||p.cells.length!==2||new Set(p.cells.map(c=>c.variant)).size!==2||p.cells.some(c=>!['baseline','candidate'].includes(c.variant))||topology(p.cells[0])!==topology(p.cells[1])))throw Error('Invalid comparison pair schedule');
  const receipts=[],cells=[];
  for(const pair of pairs){
    const receipt={pairId:pair.pairId,order:pair.cells.map(c=>c.variant),attempts:[],complete:false};
    for(const cell of pair.cells){
      const switchingStartedAt=new Date().toISOString();await switchVariant(cell);
      const dispatchStartedAt=new Date().toISOString(),result=await measure(cell),returnedAt=new Date().toISOString();
      if(cellKey(cell)!==cellKey(result)||result.complete!==true||!Number.isSafeInteger(result.expected)||result.expected<=0||result.unexpected!==0||result.timeout!==0||result.incomplete!==0)throw Error('Comparison cell incomplete: '+cellKey(cell));
      cells.push(result);receipt.attempts.push({key:cellKey(cell),variant:cell.variant,switchingStartedAt,dispatchStartedAt,returnedAt,windowStartedAt:result.windowStartedAt??null,windowEndedAt:result.windowEndedAt??null});
    }
    receipt.complete=true;receipts.push(receipt);await onPair?.(receipt);
  }
  return {complete:true,pairs:receipts,cells};
}
export function responseContractHash(bytes,status){
  if(status===200)return createHash('sha256').update(bytes).digest('hex');
  const problem=JSON.parse(bytes);if(typeof problem.traceId!=='string'||!problem.traceId)throw Error('Dynamic problem trace identifier unavailable');
  const {traceId,...contract}=problem;return createHash('sha256').update(JSON.stringify(contract)).digest('hex');
}
export function verifyManagementParity(baseline,candidate){
  const differences=[],key=s=>[s.actor??'',s.list??'',s.scale,s.pageKind,s.page,s.pageSize,s.repeat].join(':');
  const a=baseline?.samples??[],b=candidate?.samples??[];
  if(baseline?.complete!==true||candidate?.complete!==true||!a.length||a.length!==b.length||new Set(a.map(key)).size!==a.length||new Set(b.map(key)).size!==b.length)return {complete:false,differences:['coverage'],ignoredDynamicProblemFields:['traceId']};
  const right=new Map(b.map(s=>[key(s),s]));
  for(const left of a){
    const other=right.get(key(left)),status=left.status??200;
    const hash=status===200?left.responseHash:left.responseContractHash,otherHash=status===200?other?.responseHash:other?.responseContractHash;
    if(!other||status!==(other.status??200)||left.total!==other.total||JSON.stringify(left.ids??left.items)!==JSON.stringify(other.ids??other.items)||!sha.test(hash??'')||!sha.test(otherHash??'')||hash!==otherHash||status===200&&(left.responseBytes!==other.responseBytes||!Number.isSafeInteger(left.responseBytes)||left.responseBytes<=0))differences.push(key(left));
  }
  return {complete:differences.length===0,samples:a.length,differences,ignoredDynamicProblemFields:['traceId']};
}
export function verifyPairReceipts({pairs,cells,validation=false,validationSchedule}={}){
  const scheduled=validation?validationSchedule:buildComparisonPairs(),missing=[];
  if(!Array.isArray(scheduled)||!scheduled.length||!Array.isArray(pairs)||!Array.isArray(cells)||pairs.length!==scheduled.length||cells.length!==scheduled.length*2||new Set(cells.map(cellKey)).size!==cells.length)return {complete:false,diagnosticComplete:false,missing:['coverage']};
  const measured=new Map(cells.map(c=>[cellKey(c),c]));let previousReturned=-Infinity;
  for(let index=0;index<scheduled.length;index++){
    const expected=scheduled[index],actual=pairs[index];
    if(actual?.complete!==true||actual.pairId!==expected.pairId||JSON.stringify(actual.order)!==JSON.stringify(expected.cells.map(c=>c.variant))||actual.attempts?.length!==2){missing.push('order:'+expected.pairId);continue;}
    for(let side=0;side<2;side++){
      const cell=expected.cells[side],attempt=actual.attempts[side],result=measured.get(cellKey(cell));
      const times=['switchingStartedAt','dispatchStartedAt','windowStartedAt','windowEndedAt','returnedAt'].map(k=>Date.parse(attempt[k]));
      if(attempt.key!==cellKey(cell)||attempt.variant!==cell.variant||!result||attempt.windowStartedAt!==result.windowStartedAt||attempt.windowEndedAt!==result.windowEndedAt||times.some(t=>!Number.isFinite(t))||times.some((t,i)=>i>0&&t<times[i-1])||times[0]<previousReturned||times[3]-times[2]!== (validation?2500:20000))missing.push('window:'+cellKey(cell));
      previousReturned=times[4];
    }
  }
  return {complete:!validation&&missing.length===0,diagnosticComplete:missing.length===0,requiredPairs:scheduled.length,missing};
}
