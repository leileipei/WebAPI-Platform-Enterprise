import fs from 'node:fs';import assert from 'node:assert/strict';
const directory=process.env.WEBAPI_E2E_DIRECTORY,project=process.env.WEBAPI_E2E_PROJECT;
assert(project?.startsWith('webapi-enterprise-e2e-observability-')&&directory?.endsWith(project),'Explicit random observability fixture required');
const mode=process.argv[2],file='docs/evidence/observability/verification.json';fs.mkdirSync('docs/evidence/observability',{recursive:true});
const endpoints=Object.fromEntries(['CONTROL_PLANE','COLLECTOR','PROMETHEUS','LOKI','TEMPO','CONSOLE','GATEWAY_A','GATEWAY_B','BACKEND_A','BACKEND_B'].map(name=>[name.toLowerCase(),process.env['WEBAPI_'+(name==='CONTROL_PLANE'?'E2E_':'OBS_')+name+'_URL']]));
if(mode==='ready'){
 for(const[name,path]of [['prometheus','/-/ready'],['loki','/ready'],['tempo','/ready']]){const end=Date.now()+120000;while(true){try{const r=await fetch(endpoints[name]+path,{signal:AbortSignal.timeout(3000)});if(r.ok)break;}catch{}if(Date.now()>end)throw Error(name+' readiness timeout');await new Promise(r=>setTimeout(r,1000));}}
 fs.writeFileSync(directory+'/observability-context.json',JSON.stringify({project,endpoints,settings:{intervalSeconds:2,queryDelaySeconds:5,leaseSeconds:30,metricExportIntervalMs:1000,traceSampleRatio:1,queueCapacity:64}},null,2));
 fs.writeFileSync(file,JSON.stringify({complete:false,project,startedAt:new Date().toISOString(),classification:'real-isolated-gateway-three-source',mode:process.env.WEBAPI_OBS_ONLY_BOUNDARIES==='true'?'boundary-debug':'e2e',endpoints,checks:[],cleanup:null},null,2));console.log('All three real sources ready; closed-loop assertions still required.');
}else if(mode==='cleanup'){
 const e=fs.existsSync(file)?JSON.parse(fs.readFileSync(file)):{};e.complete=false;e.cleanup={exitCode:Number(process.argv[3]),secretFilesRemaining:['password','postgres-password','node-a','node-b','node-cross','node-cross2','ip-hmac','cursor-key','credential','browser-cookie'].filter(name=>fs.existsSync(directory+'/'+name)).length,containersRemaining:Number(process.argv[4]||0),volumesRemaining:Number(process.argv[5]||0)};fs.writeFileSync(file,JSON.stringify(e,null,2));
}else if(mode==='browser'){fs.writeFileSync(directory+'/browser.wait','');console.log('Browser fixture metadata: '+directory+'/observability-context.json');}else throw Error('Unknown command');
