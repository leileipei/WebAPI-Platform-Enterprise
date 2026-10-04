import test from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {validateProof,validateEndpoints} from '../../scripts/observability-smoke.mjs';
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
