import {buildMatrix} from './matrix.mjs';
export function summarize(samples,{durationMs=1000,expected=samples.length,unexpected=0,timeout=0,incomplete=0}={}){
  if(!Number.isFinite(durationMs)||durationMs<=0||samples.some(x=>!Number.isFinite(x)||x<0))throw Error('Invalid measurement');
  const sorted=[...samples].sort((a,b)=>a-b);
  const percentile=p=>sorted.length?sorted[Math.ceil(p*sorted.length)-1]:null;
  return {expected,unexpected,timeout,incomplete,rps:expected*1000/durationMs,p50:percentile(.5),p95:percentile(.95),p99:percentile(.99)};
}
export function summarizeHistogram(histogram,counts,durationMs){
  const total=histogram.reduce((s,[,n])=>s+n,0);
  const percentile=p=>{let count=0;for(const [upper,n] of histogram){count+=n;if(count>=Math.ceil(total*p))return upper;}return null;};
  return {...counts,rps:counts.expected*1000/durationMs,p50:total?percentile(.5):null,p95:total?percentile(.95):null,p99:total?percentile(.99):null};
}
export function cellKey(cell){return [cell.variant,cell.scale,cell.auth,cell.concurrency,cell.repeat,cell.nodeMode].join(':');}
export function compareResults(a,b){
  if(a.harness!==b.harness||a.dataset!==b.dataset||a.environment!==b.environment)throw Error('Comparison identity mismatch');
  if(!/^[a-f0-9]{64}$/.test(a.classificationManifestHash??'')||a.classificationManifestHash!==b.classificationManifestHash)throw Error('Comparison SQL classification identity mismatch');
  const required=buildMatrix(),cells=[...a.cells,...b.cells];
  const allKeys=cells.map(cellKey);if(new Set(allKeys).size!==allKeys.length)throw Error('Duplicate matrix cells');
  const keys=new Set(cells.filter(c=>c.complete&&c.expected>0&&!c.unexpected&&!c.timeout&&!c.incomplete).map(cellKey));
  const missing=required.filter(c=>!keys.has(cellKey(c)));
  return {complete:missing.length===0,requiredCells:required.length,completedCells:required.length-missing.length,missing:missing.map(cellKey)};
}
