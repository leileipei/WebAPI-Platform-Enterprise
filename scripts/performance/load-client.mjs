import fs from 'node:fs/promises';
import {measureCell} from './matrix.mjs';
import {cellKey} from './report.mjs';
import {waitForContainerSampler} from './resources.mjs';

const cell=JSON.parse(await fs.readFile('/private/cell.json','utf8'));
if(![100,1000,10000].includes(cell.scale)||!['anonymous','api-key','missing-key','rs256','es256','wrong-secret','no-mapping','no-grant'].includes(cell.auth)||!['dual','gateway-a','gateway-b'].includes(cell.nodeMode))throw Error('Invalid private cell');
const token=(await fs.readFile('/private/probe-token','utf8')).trim();
const requests=JSON.parse(await fs.readFile(`/private/scales/${cell.scale}/requests.json`,'utf8'))[cell.auth];
const urls=(cell.nodeMode==='dual'?['gateway-a','gateway-b']:[cell.nodeMode]).map(name=>`http://${name}:8080`);
const servers={};let timer;const samples=[];
async function state(){
  const result={};
  for(const name of ['gateway-a','gateway-b','controlplane','backend']) {
    const response=await fetch(`http://${name}:8080/__perf/state`,{headers:{'X-Performance-Token':token},signal:AbortSignal.timeout(5000)});
    if(!response.ok)throw Error('Private server state unavailable');result[name]=await response.json();
  }
  return result;
}
let sampling=false;
const onMeasurementStart=async()=>{
  await waitForContainerSampler(`/results/${cellKey(cell)}.resources-ready.json`);
  servers.before=await state();timer=setInterval(async()=>{if(sampling)return;sampling=true;try{samples.push({time:new Date().toISOString(),servers:await state()});}catch{samples.push({failed:true});}finally{sampling=false;}},500);timer.unref();
};
const onMeasurementEnd=async()=>{clearInterval(timer);while(sampling)await new Promise(resolve=>setTimeout(resolve,5));servers.after=await state();};
const result=await measureCell({urls,requests,concurrency:cell.concurrency,warmupMs:cell.validation?50:10000,measureMs:cell.validation?2500:20000,onMeasurementStart,onMeasurementEnd});
const measuredSamples=samples.filter(s=>s.time&&s.time>=result.windowStartedAt&&s.time<=result.windowEndedAt);
const output={...cell,...result,servers,samples,measuredSamples,serverCounterScope:'before-measurement-through-drain',samplingComplete:!samples.some(s=>s.failed)};
await fs.writeFile(`/results/${cellKey(cell)}.json`,JSON.stringify(output,null,2)+'\n');
if(result.unexpected||result.timeout||result.incomplete||!result.complete||result.expected===0||!output.samplingComplete)process.exitCode=4;
