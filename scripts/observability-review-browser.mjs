// Disposable reader only; never changes persistent users or permissions.
import fs from 'node:fs';import assert from 'node:assert/strict';
import {scenario,sleep} from './observability-scenario.mjs';
const s=await scenario(),action=process.argv[2];
if(action==='reader'){
 const c=await s.loginActor('e2e-reader');
 const cookie=c.cookies.get('WebApi.Session');assert(cookie,'Fixture reader session cookie missing');
 fs.writeFileSync(s.directory+'/browser-cookie','WebApi.Session='+cookie,{mode:0o600});
 console.log('Production preview now uses the disposable read-only actor.');
}else if(action==='revoke'||action==='restore'){
 const reader=(await s.list('/users')).find(u=>u.username==='e2e-reader');assert(reader);
 await s.admin.request('/users/'+reader.id+'/scopes',{method:'PUT',headers:{'If-Match':`"${reader.revision}"`},body:{scopes:action==='revoke'?[]:[{scope:{organizationId:s.context.organizationId},accessMode:'read'}]}});
 fs.writeFileSync(s.directory+'/reader-'+action+'.json',JSON.stringify({project:s.project,actorId:reader.id,at:new Date().toISOString()},null,2));
 console.log('Disposable reader '+action+' completed.');
}else if(action==='traffic'){
 await s.publish('A');for(let i=0;i<65;i++){await Promise.all([s.request(0),s.request(1)]);await sleep(1000);}
 console.log('Actual approved release and both gateway traffic prepared.');
}else throw Error('Unknown review browser action');
