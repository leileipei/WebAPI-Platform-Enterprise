import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {spawn} from 'node:child_process';

// Exercise the real entry point and real clocks/files. Substitute only the
// external HTTP workload and Docker barrier so a fast completed query batch
// reproduces the insufficient resource-window bug without a live database.
async function runClient({scene='reference',validation=true,explain=true,delayMs=10,complete=true}={}) {
  const root=await fs.mkdtemp(path.join(os.tmpdir(),'webapi-management-window-'));
  const entry=new URL('./management-client.mjs',import.meta.url).href;
  await fs.mkdir(path.join(root,'private'));await fs.mkdir(path.join(root,'results'));
  for(const [name,value] of Object.entries({'probe-token':'fixture-token','admin-password':'fixture-password','scope-100.json':'{}','reference-100.json':'{}'}))
    await fs.writeFile(path.join(root,'private',name),value);
  const bootstrap=`
    import {registerHooks} from 'node:module';
    const entry=${JSON.stringify(entry)},root=${JSON.stringify(root)};
    const workload=${JSON.stringify({scene,validation,explain,delayMs,complete})};
    const sources={
      'fixture:files':\`import fs from 'node:fs/promises';const root=\${JSON.stringify(root)};export default {readFile:(p,...a)=>fs.readFile(root+p,...a),writeFile:(p,...a)=>fs.writeFile(root+p,...a)};\`,
      'fixture:barrier':\`export async function waitForContainerSampler(){}\`,
      'fixture:workload':\`import {setTimeout} from 'node:timers/promises';const fixture=\${JSON.stringify(workload)};async function measure(options){if(options.validation!==fixture.validation||options.explain!==fixture.explain)throw Error('Fixture option mismatch');await setTimeout(fixture.delayMs);return {complete:fixture.complete,samples:[{durationMs:17,sql:{total:7,authorization:0,data:0,unclassified:7,rows:9},shapes:[]}],fixtureScene:fixture.scene};}export {measure as measureManagement,measure as measureScopeManagement,measure as measureReferenceManagement};\`
    };
    registerHooks({
      resolve(specifier,context,next){
        if(context.parentURL===entry){
          const replacement=specifier==='node:fs/promises'?'fixture:files':specifier==='./resources.mjs'?'fixture:barrier':['./management.mjs','./scopes.mjs','./references.mjs'].includes(specifier)?'fixture:workload':null;
          if(replacement)return {url:replacement,shortCircuit:true};
        }
        return next(specifier,context);
      },
      load(url,context,next){return sources[url]?{format:'module',source:sources[url],shortCircuit:true}:next(url,context);}
    });
    process.argv=['node','management-client',${JSON.stringify(scene)},'100',${JSON.stringify(String(validation))},${JSON.stringify(String(explain))}];
    await import(entry);
  `;
  try {
    const child=spawn(process.execPath,['--input-type=module','--eval',bootstrap],{stdio:['ignore','pipe','pipe']});
    let stderr='';child.stderr.on('data',chunk=>stderr+=chunk);
    const status=await new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',resolve);});
    const result=JSON.parse(await fs.readFile(path.join(root,'results',`${scene==='ordinary'?'management':scene}-queries-100.json`),'utf8'));
    return {status,stderr,result};
  } finally {await fs.rm(root,{recursive:true,force:true});}
}

test('fast management batches retain a sufficient resource window without padding query or client workload metrics',async()=>{
  for(const scene of ['ordinary','scope','reference']) {
    const {status,stderr,result}=await runClient({scene});
    assert.equal(status,0,stderr);
    assert.ok(Date.parse(result.windowEndedAt)-Date.parse(result.windowStartedAt)>=2500,'fast batch must leave two in-window Docker samples');
    assert.equal(result.samples.length,1);assert.equal(result.samples[0].durationMs,17);assert.equal(result.samples[0].sql.total,7);
    assert.ok(result.client.wallMs<1000,'client workload wall time must exclude the idle resource tail');
    assert.ok(result.resourceWindowPaddingMs>1500);
    assert.ok(Date.parse(result.workloadWindowEndedAt)<Date.parse(result.windowEndedAt));
    assert.equal(result.client.cpuMeasurementScope,'login-queries-sql-probe-and-classification-without-resource-tail');
  }
});

test('formal management batches use the same resource minimum and preserve failed completion',async()=>{
  for(const complete of [true,false]) {
    const {status,stderr,result}=await runClient({validation:false,explain:false,complete});
    assert.equal(status,complete?0:4,stderr);assert.equal(result.complete,complete);
    assert.ok(Date.parse(result.windowEndedAt)-Date.parse(result.windowStartedAt)>=2500);
    assert.equal(result.samples.length,1);assert.equal(result.samples[0].sql.total,7);
    assert.ok(result.resourceWindowPaddingMs>1500);
    assert.equal(result.measurementScope,'login-queries-sql-probe-and-classification');
    assert.equal(result.resourceMeasurementScope,'workload-and-explicit-idle-tail');
  }
});

test('management workload already longer than the resource minimum adds no idle tail',async()=>{
  const {status,stderr,result}=await runClient({delayMs:2700});assert.equal(status,0,stderr);
  assert.ok(result.client.wallMs>=2700);assert.equal(result.resourceWindowPaddingMs,0);
  assert.equal(result.resourceMeasurementScope,'workload-only');
  assert.equal(result.measurementScope,'explain-diagnostic-not-performance');
  assert.equal(result.samples[0].durationMs,17);
});
