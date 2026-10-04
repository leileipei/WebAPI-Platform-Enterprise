import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import {randomUUID} from 'node:crypto';
import {initializeRuntime,operateRuntime} from '../../../scripts/runtime/lifecycle.mjs';
import {loadState} from '../../../scripts/runtime/state.mjs';
import {prepareContext} from '../../../scripts/runtime/context.mjs';
import {inspectResources,assertOwnership,docker} from '../../../scripts/runtime/docker.mjs';
import {createBackup,dataVolumes} from '../../../scripts/runtime/acceptance-backup.mjs';
import {configureRuntime} from '../../../scripts/runtime/configure.mjs';
import {LocalClient} from '../../../scripts/local-client.mjs';

test('real backups cover unbound, bound without demo and bound with demo',async()=>{
 const project='webapi-enterprise-local-test-'+randomUUID(),directory=path.resolve('.runtime/tests/'+project);
 const servers=await Promise.all([0,1,2].map(async()=>{const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));return s;}));
 const [consolePort,gatewayA,gatewayB]=servers.map(s=>s.address().port);await Promise.all(servers.map(s=>new Promise(r=>s.close(r))));
 let state;
 const context=async()=>prepareContext(directory,state,JSON.parse(await fs.readFile(path.join(directory,'release.json'),'utf8')));
 const backup=async label=>{const ctx=await context(),target=path.join(directory,'backup-'+label);await createBackup(ctx,target);const m=JSON.parse(await fs.readFile(path.join(target,'backup-manifest.json'),'utf8'));for(const name of dataVolumes)assert.ok((await fs.stat(path.join(target,name+'.tar'))).size>0);assert.ok(m.files['postgres.dump']);const after=await inspectResources(state);assert.ok(after.filter(r=>r.Kind==='container').every(r=>!r.State.Running));const result=await operateRuntime('start',state,{directory});assert.equal(result.phase,state.binding?'Registered':'Unconfigured');};
 try{
  state=await initializeRuntime({directory,projectName:project,ports:{console:consolePort,gatewayA,gatewayB},bootstrapUsername:'backup-test-admin',releaseFile:path.resolve('.runtime/local-build/release.json')});
  await operateRuntime('stop',state,{directory});await backup('unbound-already-stopped');
  const passwordFile=path.join(directory,'secrets/bootstrap-password'),client=new LocalClient('http://127.0.0.1:'+consolePort);await client.login(state.bootstrapUsername,(await fs.readFile(passwordFile,'utf8')).trimEnd());
  const org=await client.request('/organizations',{method:'POST',body:{code:'BACKUP_TEST',name:'Backup test'}}),projectRow=await client.request('/organizations/'+org.id+'/projects',{method:'POST',body:{code:'BACKUP_TEST',name:'Backup test'}}),environment=await client.request('/projects/'+projectRow.id+'/environments',{method:'POST',body:{code:'BACKUP_TEST',name:'Backup test'}});
  const options={environmentId:environment.id,allowedOrigins:['http://backend-a:8080','http://backend-b:8080'],demoEnabled:false,username:state.bootstrapUsername,passwordFile};
  state=await configureRuntime(state,options,{directory});await backup('bound-without-demo');
  state=await configureRuntime(state,{...options,demoEnabled:true},{directory});await backup('bound-with-demo');
 }finally{
  state??=await loadState(directory);assertOwnership(state,await inspectResources(state));const ctx=await context();await ctx.compose('--profile','tools','down','--volumes','--remove-orphans');assert.equal((await inspectResources(state)).length,0);await fs.rm(directory,{recursive:true,force:true});
 }
});

test('unlabelled exact data volume blocks initialization without adoption',async()=>{
 const project='webapi-enterprise-local-test-'+randomUUID(),directory=path.resolve('.runtime/tests/'+project),name=project+'_pg';
 let created=false;
 try{await docker(['volume','create',name]);created=true;const before=JSON.parse((await docker(['volume','inspect',name])).stdout);await assert.rejects(initializeRuntime({directory,projectName:project,bootstrapUsername:'ownership-test-admin'}),/project|owner/i);assert.deepEqual(JSON.parse((await docker(['volume','inspect',name])).stdout),before);assert.equal((await inspectResources({projectName:project})).length,1);await assert.rejects(fs.access(path.join(directory,'secrets')));}
 finally{if(created)await docker(['volume','rm',name]);await fs.rm(directory,{recursive:true,force:true});}
});
