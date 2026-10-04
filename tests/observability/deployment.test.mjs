import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {validateProof,validateEndpoints,validateMetricContract} from '../../scripts/observability-smoke.mjs';
const compose='deploy/compose.observability.yml';
test('observability deployment exposes only loopback diagnostics and pins every runtime image',()=>{
 const run=spawnSync('docker',['compose','-f',compose,'config','--format','json'],{encoding:'utf8'});
 assert.equal(run.status,0,run.stderr);
 const c=JSON.parse(run.stdout);
 assert.deepEqual(Object.keys(c.services).sort(),['collector','loki','prometheus','tempo']);
 for(const service of Object.values(c.services)){
  assert.match(service.image,/@sha256:[a-f0-9]{64}$/);
  for(const p of service.ports||[])assert.equal(p.host_ip,'127.0.0.1');
 }
});
test('fixed metric names need actual histogram and node gauge ingestion proof',()=>{
 assert.throws(()=>validateMetricContract({histogramCount:0,upperBucketFound:true,nodeObservedSeconds:1}),/Metric contract/);
 assert.throws(()=>validateMetricContract({histogramCount:1,upperBucketFound:false,nodeObservedSeconds:1}),/Metric contract/);
 assert.throws(()=>validateMetricContract({histogramCount:1,upperBucketFound:true,nodeObservedSeconds:NaN}),/Metric contract/);
 assert.doesNotThrow(()=>validateMetricContract({histogramCount:1,upperBucketFound:true,nodeObservedSeconds:1}));
});
test('readiness or incomplete ingestion cannot be accepted as three-source proof',()=>{
 for(const proof of [
  {requestCounter:0,logMarkerFound:true,traceFound:true},
  {requestCounter:1,logMarkerFound:false,traceFound:true},
  {requestCounter:1,logMarkerFound:true,traceFound:false},
  {requestCounter:NaN,logMarkerFound:true,traceFound:true}
 ])assert.throws(()=>validateProof(proof),/must all be found/);
 assert.doesNotThrow(()=>validateProof({requestCounter:1,logMarkerFound:true,traceFound:true}));
});
test('missing or invalid telemetry pipeline rejects smoke rather than claiming health is success',()=>{
 const run=spawnSync('bash',['scripts/check-observability.sh','unsupported'],{encoding:'utf8'});
 assert.equal(run.status,2);
 assert.match(run.stderr,/Unknown check kind/);
});

test('invalid published endpoint fails immediately and only loopback HTTP is allowed',()=>{
 for(const endpoint of ['http://invalid IP:0','http://127.0.0.1:0','http://0.0.0.0:3200','https://example.org:3200'])
  assert.throws(()=>validateEndpoints([endpoint]),/Invalid loopback endpoint/);
 assert.doesNotThrow(()=>validateEndpoints(['http://127.0.0.1:33600']));
});
test('real log contract requires normalized metadata and safe projected method/path fields',async()=>{
 const {validateLogContract}=await import('../../scripts/observability-smoke.mjs');
 assert.throws(()=>validateLogContract({metadataFound:false,normalizedNames:false,filterFound:false}));
 assert.doesNotThrow(()=>validateLogContract({metadataFound:true,normalizedNames:true,filterFound:true}));
});
test('trace proof requires actual server/client scope, safe attributes, parent and search',async()=>{
 const {validateTraceContract}=await import('../../scripts/observability-smoke.mjs');const valid={serverFound:true,clientFound:true,resourceScopeFound:true,parentFound:true,safeAttributes:true,searchFound:true};assert.doesNotThrow(()=>validateTraceContract(valid));for(const key of Object.keys(valid))assert.throws(()=>validateTraceContract({...valid,[key]:false}));
});
