import fs from 'node:fs/promises';
import {performance,monitorEventLoopDelay} from 'node:perf_hooks';
import {measureManagement} from './management.mjs';
import {measureScopeManagement} from './scopes.mjs';
import {measureReferenceManagement} from './references.mjs';
import {createHash} from 'node:crypto';
import {classifyMeasurement} from './classification.mjs';
import {waitForContainerSampler} from './resources.mjs';
const [scene,scaleText,validationText,explainText='false',...extra]=process.argv.slice(2),scale=Number(scaleText);
if(!['ordinary','scope','reference'].includes(scene)||![100,1000,10000].includes(scale)||!['true','false'].includes(validationText)||!['true','false'].includes(explainText)||extra.length)throw Error('Invalid private management cell');
const token=(await fs.readFile('/private/probe-token','utf8')).trim(),password=(await fs.readFile('/private/admin-password','utf8')).trim();
const options={baseUrl:'http://controlplane:8080',token,password,scale,seed:20261009,validation:validationText==='true',explain:explainText==='true'};
await waitForContainerSampler(`/results/${scene}-${scale}.resources-ready.json`);
const windowStartedAt=new Date().toISOString();
const before=process.cpuUsage(),start=performance.now(),delays=monitorEventLoopDelay({resolution:10});delays.enable();
let peakRssBytes=process.memoryUsage().rss;const timer=setInterval(()=>{peakRssBytes=Math.max(peakRssBytes,process.memoryUsage().rss);},100);timer.unref();
try {
  let measured=scene==='ordinary'?await measureManagement(options):await (scene==='scope'?measureScopeManagement:measureReferenceManagement)({...options,manifest:JSON.parse(await fs.readFile(`/private/${scene}-${scale}.json`,'utf8'))});
  let classificationInput=null;try{classificationInput=JSON.parse(await fs.readFile('/private/sql-classification-input.json','utf8'));}catch(error){if(error.code!=='ENOENT')throw error;}
  if(classificationInput){
    const {manifestJson,manifestHash,sourceCommit}=classificationInput;if(createHash('sha256').update(manifestJson).digest('hex')!==manifestHash)throw Error('SQL classification input hash mismatch');
    measured=classifyMeasurement(measured,{manifest:JSON.parse(manifestJson),sourceCommit,manifestHash});
  }
  measured.windowStartedAt=windowStartedAt;measured.windowEndedAt=new Date().toISOString();
  measured.measurementScope=options.explain?'explain-diagnostic-not-performance':'login-queries-sql-probe-and-classification';
  const cpu=process.cpuUsage(before),wallMs=performance.now()-start;
  measured.client={version:process.version,architecture:process.arch,peakRssBytes,cpuUserUs:cpu.user,cpuSystemUs:cpu.system,cpuCores:(cpu.user+cpu.system)/(wallMs*1000),eventLoopP95Ms:delays.percentile(95)/1e6,wallMs};
  await fs.writeFile(`/results/${scene==='ordinary'?'management':scene}-queries-${scale}.json`,JSON.stringify(measured,null,2)+'\n');
  await waitForContainerSampler(`/results/${scene}-${scale}.resources-finished.json`);
  if(!measured.complete)process.exitCode=4;
}catch(error){process.stderr.write('Management fixture failed: '+error.message+'\n');process.exitCode=4;}
finally{clearInterval(timer);delays.disable();}
