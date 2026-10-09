const types=new Set(['Result','ProjectSet','ModifyTable','Append','Merge Append','Recursive Union','BitmapAnd','BitmapOr','Seq Scan','Sample Scan','Index Scan','Index Only Scan','Bitmap Index Scan','Bitmap Heap Scan','Tid Scan','Tid Range Scan','Subquery Scan','Function Scan','Table Function Scan','Values Scan','CTE Scan','Named Tuplestore Scan','WorkTable Scan','Foreign Scan','Custom Scan','Nested Loop','Merge Join','Hash Join','Materialize','Memoize','Sort','Incremental Sort','Group','Aggregate','WindowAgg','Unique','Gather','Gather Merge','Hash','SetOp','LockRows','Limit']);
const metricNames=new Set(['Startup Cost','Total Cost','Plan Rows','Plan Width','Actual Startup Time','Actual Total Time','Actual Rows','Actual Loops','Rows Removed by Filter','Rows Removed by Index Recheck','Rows Removed by Join Filter','Shared Hit Blocks','Shared Read Blocks','Shared Dirtied Blocks','Shared Written Blocks','Local Hit Blocks','Local Read Blocks','Local Dirtied Blocks','Local Written Blocks','Temp Read Blocks','Temp Written Blocks','I/O Read Time','I/O Write Time','Temp I/O Read Time','Temp I/O Write Time','Sort Space Used','Peak Memory Usage','Hash Buckets','Original Hash Buckets','Hash Batches','Original Hash Batches','Heap Fetches','Workers Planned','Workers Launched']);
const fail=()=>{throw Error('Invalid EXPLAIN evidence');};
const exact=(object,names)=>{if(!object||typeof object!=='object'||Array.isArray(object)||Object.keys(object).sort().join('|')!==[...names].sort().join('|'))fail();};
const nonnegative=value=>typeof value==='number'&&Number.isFinite(value)&&value>=0;
export function verifyExplainCompletion({scales,ordinary,scope,reference,cleanup,secretScan}){
  if(!Array.isArray(scales)||!scales.length||new Set(scales).size!==scales.length||scales.some(s=>![100,10000].includes(s))||cleanup?.remaining!==0||cleanup.privateInputsDeleted!==true||secretScan?.violations!==0)return false;
  return [[ordinary,156],[scope,150],[reference,24]].every(([scenes,count])=>Array.isArray(scenes)&&scenes.length===scales.length&&new Set(scenes.map(s=>s.scale)).size===scales.length&&scales.every(scale=>scenes.some(s=>s.scale===scale&&s.complete===true&&Array.isArray(s.samples)&&s.samples.length===count&&s.samples.every(sample=>sample.explain?.complete===true))));
}
export async function collectExplain(request,id,sql){
  if(!/^[a-f0-9]{32}$/.test(id)||!Number.isSafeInteger(sql?.stats?.total)||sql.stats.total<0||!Array.isArray(sql.shapes))fail();
  const response=await request('/__perf/explain/'+id);if(!response.ok)throw Error('EXPLAIN diagnostic probe unavailable');
  const packet=await response.json();exact(packet,['explained','repeated','queries']);
  if(!Number.isSafeInteger(packet.explained)||packet.explained<0||!Number.isSafeInteger(packet.repeated)||packet.repeated<0||!Array.isArray(packet.queries)||packet.explained!==packet.queries.length||packet.explained+packet.repeated>sql.stats.total)fail();
  const readerShapes=sql.shapes.filter(s=>s.kind==='reader'),readerHashes=new Set(readerShapes.map(s=>s.hash)),seen=new Set();let nodes=0;
  if(sql.shapes.some(s=>!Number.isSafeInteger(s.count)||s.count<1)||sql.shapes.reduce((n,s)=>n+s.count,0)!==sql.stats.total||readerShapes.reduce((n,s)=>n+s.count,0)!==packet.explained+packet.repeated||readerHashes.size!==packet.explained)fail();
  const node=(value,depth=0)=>{
    exact(value,['nodeType','relation','index','metrics','children']);
    if(++nodes>512*packet.queries.length||depth>32||!types.has(value.nodeType)||!Array.isArray(value.children))fail();
    for(const name of ['relation','index'])if(value[name]!==null&&!(typeof value[name]==='string'&&/^[A-Za-z_][A-Za-z0-9_]{0,126}~?$/.test(value[name])))fail();
    if(!value.metrics||typeof value.metrics!=='object'||Array.isArray(value.metrics)||Object.entries(value.metrics).some(([key,value])=>!metricNames.has(key)||!nonnegative(value)))fail();
    for(const child of value.children)node(child,depth+1);
  };
  for(const query of packet.queries){
    exact(query,['hash','plan']);if(!/^[a-f0-9]{64}$/.test(query.hash)||!readerHashes.has(query.hash)||seen.has(query.hash))fail();seen.add(query.hash);
    exact(query.plan,['planningMs','executionMs','plan']);if(!nonnegative(query.plan.planningMs)||!nonnegative(query.plan.executionMs))fail();node(query.plan.plan);
  }
  if(readerHashes.size>0&&packet.explained===0)fail();
  return {...packet,skippedCommands:sql.stats.total-packet.explained-packet.repeated,measurementScope:'explain-diagnostic-not-performance',complete:true};
}
