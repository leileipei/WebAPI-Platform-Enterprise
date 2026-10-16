import {postgresImage} from './template-fixture.mjs';
import {approvedIndexes} from './index-experiment.mjs';
import {buildMatrix} from './matrix.mjs';
import {cellKey} from './report.mjs';
import {verifyContainerResources} from './resources.mjs';
const sha=/^[a-f0-9]{64}$/,commit=/^[a-f0-9]{40}$/,scales=[100,1000,10000];
const lists=['api','cluster','route','application','policy','user','node','node-event','snapshot','release','alert-rule','alert-event','policy-reference'];
const actors=[['platform-role',200,200],['organization-role',200,200],['project-read-only',200,200],['environment-read-only',200,200],['no-function',200,404],['no-scope',200,404],['inactive-user',401,401],['inactive-ancestor',200,404],['cross-organization-role',200,404]];
const operations=['route','cluster','application','credential','authentication','jwt_mapping','authorization'];
const positions=['first','middle','last'];
const finite=n=>Number.isFinite(n)&&n>=0,integer=n=>Number.isSafeInteger(n)&&n>=0;
function sameKeys(actual,required){return Array.isArray(actual)&&actual.length===required.length&&new Set(actual).size===actual.length&&required.every(key=>actual.includes(key));}
function pageKeys(prefix){return [1,50,100].flatMap(size=>['first','middle','last','out-of-range'].flatMap(kind=>[1,2,3].map(repeat=>[...prefix,size,kind,repeat].join(':'))));}
function serverState(value){return value&&['heapBytes','workingSetBytes','totalAllocatedBytes','pauseTicks','cpuTicks'].every(k=>finite(value[k]))&&Array.isArray(value.collections)&&value.collections.length===3&&value.collections.every(integer);}
function serversValid(value){return ['gateway-a','gateway-b','controlplane','backend'].every(name=>serverState(value?.[name]));}
function sqlValid(sample,source){
  const {sql,rawSql,shapes}=sample;
  if(!sql||!rawSql||!Array.isArray(shapes)||!integer(sql.total)||sql.total!==rawSql.total||sql.unclassified!==0||!integer(sql.authorization)||!integer(sql.data)||sql.authorization+sql.data!==sql.total||!integer(sql.rows)||sql.rows!==rawSql.rows||!integer(sql.binaryColumnBytes)||sql.binaryColumnBytes!==rawSql.binaryColumnBytes)return false;
  let auth=0,data=0;
  for(const s of shapes){
    if(!sha.test(s.hash??'')||!Number.isSafeInteger(s.count)||s.count<1||!['reader','scalar','nonquery'].includes(s.kind)||!Array.isArray(s.sourceEvidence)||!s.sourceEvidence.some(ref=>ref.commit===source&&/^src\/.+\.cs$/.test(ref.path??'')&&Number.isSafeInteger(ref.line)&&ref.line>0))return false;
    if(s.classification==='authorization')auth+=s.count;else if(s.classification==='data')data+=s.count;else return false;
  }
  return auth===sql.authorization&&data===sql.data;
}
// Evidence completeness only. Regression, performance gain and deployment acceptance
// are separate gates; a mutable self-check can never close this baseline gate.
export function verifyBaselineCompletion(input={}){
  const missing=[],check=(condition,reason)=>{if(!condition)missing.push(reason);};
  const identity=input.identity??{},env=input.environment??{};
  const variant=input.variant??'baseline';
  check((input.mode==='baseline'&&variant==='baseline'||input.mode==='compare'&&['baseline','candidate'].includes(variant))&&input.immutable===true,'immutable-baseline-mode');
  check(['source','harness','productTree','harnessTree'].every(k=>commit.test(identity[k]??''))&&sha.test(identity.harnessFingerprint??'')&&Object.keys(identity.harnessFiles??{}).length>0&&Object.values(identity.harnessFiles??{}).every(h=>sha.test(h)),'pinned-source-and-harness');
  check(sha.test(input.classificationManifestHash??''),'pinned-sql-classification');
  check(env.verified===true&&sha.test(env.fingerprint??'')&&sameKeys(Object.keys(env.services??{}),['gateway-a','gateway-b','controlplane','backend','postgres','redis','client'])&&['gateway-a','gateway-b','controlplane'].every(n=>env.runtimeState?.[n]?.serverGc===true&&env.runtimeState[n].processorCount===2)&&Object.entries({maxConnections:'100',sharedBuffers:'512MB',workMem:'16MB',maintenanceWorkMem:'256MB',parallelWorkersPerGather:'0',timezone:'UTC'}).every(([k,v])=>env.databaseActual?.[k]===v),'verified-runtime-environment');
  if(input.experiment==='current-business-combined-v1'){
    const p=input.combinedProof;
    check(p?.sourceDiffVerified===true&&sha.test(p.sourceDiffSha256??'')&&p.baselineSource!==p.candidateSource&&p.candidateSource==='760493da5a5bc0f0674e7e6398260c8a5ac4a6c0'&&p.candidateProductTree==='1aa393776ac219f43c7a9de48a63632dce1c82bf'&&p.harness===identity.harness&&p.harnessFingerprint===identity.harnessFingerprint&&identity.source===(variant==='baseline'?p.baselineSource:p.candidateSource),'combined-source-difference');
    const validRole=proof=>proof?.complete===true&&proof.experimentRoleVerified===true&&proof.role===variant&&proof.modelMatched===(variant==='candidate')&&sha.test(proof.contractSha256??'')&&sha.test(proof.metadataSha256??'')&&sha.test(proof.indexesSha256??'')&&Array.isArray(proof.missingApprovedIndexes)&&sameKeys(proof.missingApprovedIndexes,variant==='baseline'?approvedIndexes.map(i=>i.name):[]);
    const validTemplate=(proof,key)=>{
      const receipt=proof?.receipt,owner=input.cleanup?.owner;
      return proof?.target?.complete===true&&proof.target.checkedBy==='migration-current-contract'&&sha.test(proof.target.schemaHash??'')&&proof.restored===true
        &&/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/.test(owner??'')
        &&receipt?.schemaVersion===1&&receipt.owner===owner&&receipt.key===key&&receipt.sourceProduct===p?.candidateSource&&receipt.postgresImage===postgresImage
        &&['contractSha256','metadataSha256','privateContractSha256'].every(k=>sha.test(receipt[k]??''))&&validRole(proof.roleProof);
    };
    const proofs=Array.isArray(p?.fixtures)?p.fixtures:[];
    check(sameKeys(proofs.map(x=>x.scene+':'+x.scale),['ordinary','scope','reference'].flatMap(scene=>[100,1000,10000].map(scale=>scene+':'+scale)))&&proofs.every(x=>validTemplate(x,x.scene+'-'+x.scale)),'combined-target-template-role');
    check(validTemplate(p?.gateway,'gateway-controlplane-100'),'combined-gateway-control-database');
    check(Array.isArray(input.micro)&&input.micro.length===3&&input.micro.every(m=>m.entries?.every(e=>e.backend===(variant==='baseline'?'runtime-linear-map-v1':'runtime-frozen-dictionary-v1'))),'combined-actual-runtime-backend');
  }
  const cells=Array.isArray(input.cells)?input.cells:[];
  check(sameKeys(cells.map(cellKey),buildMatrix({variants:[variant]}).map(cellKey)),'gateway-matrix-144-cells');
  for(const c of cells){
    check(verifyContainerResources(c.containerResources,{start:c.windowStartedAt,end:c.windowEndedAt}),'gateway-container-window:'+cellKey(c));
    const start=Date.parse(c.windowStartedAt),end=Date.parse(c.windowEndedAt),key=cellKey(c);
    check(c.validation!==true&&c.complete===true&&integer(c.expected)&&c.expected>0&&c.unexpected===0&&c.timeout===0&&c.incomplete===0&&c.durationMs===20000&&c.warmupMs===10000&&end-start===20000&&['p50','p95','p99'].every(k=>finite(c[k]))&&Array.isArray(c.histogram)&&c.histogram.every(([upper,n])=>finite(upper)&&integer(n)&&n>0)&&c.histogram.reduce((sum,[,n])=>sum+n,0)===c.expected,'gateway-window:'+key);
    check(Array.isArray(c.nodes)&&c.nodes.length===(c.nodeMode==='dual'?2:1)&&c.nodes.every(n=>integer(n.expected)&&n.expected>0&&n.unexpected===0&&n.timeout===0&&n.incomplete===0)&&c.nodes.reduce((sum,n)=>sum+n.expected,0)===c.expected,'gateway-node-evidence:'+key);
    check(c.samplingComplete===true&&serversValid(c.servers?.before)&&serversValid(c.servers?.after)&&Array.isArray(c.measuredSamples)&&c.measuredSamples.length>0&&c.measuredSamples.every(s=>Date.parse(s.time)>=start&&Date.parse(s.time)<=end&&serversValid(s.servers))&&['peakRssBytes','cpuUserUs','cpuSystemUs','eventLoopP95Ms','maxSchedulingDelayMs'].every(k=>finite(c.client?.[k])),'gateway-resource-samples:'+key);
  }
  const byScale=(name,key='scale')=>{
    const entries=Array.isArray(input[name])?input[name]:[];
    check(sameKeys(entries.map(x=>x[key]??x.spec?.[key]),scales),name+'-all-scales');return entries;
  };
  for(const dataset of byScale('gatewayDatasets'))check(sha.test(dataset.fingerprint??'')&&['anonymous','apikey','rs256','es256','no-grant'].every(n=>Number.isSafeInteger(dataset.snapshotBytes?.[n])&&dataset.snapshotBytes[n]>0&&dataset.snapshotBytes[n]<=16777216&&sha.test(dataset.snapshotHashes?.[n]??'')),'gateway-wire:'+dataset.spec?.scale);
  for(const name of ['management','scopeManagement','referenceManagement'])for(const measured of byScale(name)){
    check(verifyContainerResources(measured.containerResources,{start:measured.windowStartedAt,end:measured.windowEndedAt}),name+'-container-window:'+measured.scale);
    const samples=Array.isArray(measured.samples)?measured.samples:[];
    const prefix=s=>name==='management'?[s.list]:name==='scopeManagement'?[s.actor,s.list,s.status]:[s.actor];
    const required=name==='management'?lists.flatMap(list=>pageKeys([list])):name==='referenceManagement'?['platform-role','environment-read-only'].flatMap(actor=>pageKeys([actor])):actors.flatMap(([actor,a,p])=>[['application',a],['policy',p]].flatMap(([list,status])=>status===200?pageKeys([actor,list,status]):[1,2,3].map(repeat=>[actor,list,status,1,'denied',repeat].join(':'))));
    check(measured.complete===true&&sameKeys(samples.map(s=>[...prefix(s),s.pageSize,s.pageKind,s.repeat].join(':')),required)&&samples.every(s=>s.scale===measured.scale&&finite(s.durationMs)&&integer(s.responseBytes)&&sha.test(s.responseHash??'')),name+'-coverage:'+measured.scale);
    check(measured.classificationComplete===true&&measured.classificationManifest?.hash===input.classificationManifestHash&&measured.classificationManifest?.sourceCommit===identity.source&&Array.isArray(measured.classificationManifest?.unclassifiedHashes)&&measured.classificationManifest.unclassifiedHashes.length===0&&samples.every(s=>sqlValid(s,identity.source)),name+'-sql:'+measured.scale);
  }
  for(const measured of byScale('micro'))check(sameKeys((measured.entries??[]).map(e=>e.operation+':'+e.position),operations.flatMap(o=>positions.map(p=>o+':'+p)))&&measured.entries.every(e=>e.mode===(variant==='candidate'?'indexed':'scan')&&e.iterations===10000&&e.hits===10000&&finite(e.elapsedTicks)&&e.elapsedTicks>0&&integer(e.allocatedBytes)),'lookup-micro:'+measured.scale);
  for(const measured of byScale('generation'))check(['emptyHeapBytes','singleHeapBytes','heldDualHeapBytes','releasedHeapBytes'].every(k=>finite(measured[k]))&&integer(measured.newBuild?.allocatedBytes)&&finite(measured.newBuild?.heapBytes)&&/^\d{2}:\d{2}:\d{2}\.\d+$/.test(measured.newBuild?.elapsed??'')&&measured.oldLeaseUsable===true&&measured.retiredRemoved===true&&measured.retiredCollected===true,'generation-lifecycle:'+measured.scale);
  check(input.cleanup?.remaining===0&&input.cleanup?.privateInputsDeleted===true,'owner-cleanup');
  check(input.secretScan?.violations===0&&input.secretScan?.filesScanned>0&&Array.isArray(input.secretScan?.findings)&&input.secretScan.findings.length===0,'public-secret-scan');
  return {complete:missing.length===0,requiredGatewayCells:144,missing};
}
