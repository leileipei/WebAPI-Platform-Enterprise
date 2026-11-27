import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {freePorts} from '../../../scripts/runtime/acceptance.mjs';
import {initializeRuntime, operateRuntime, runtimeServices} from '../../../scripts/runtime/lifecycle.mjs';
import {inspectResources, docker} from '../../../scripts/runtime/docker.mjs';
import {createBackup, dataVolumes} from '../../../scripts/runtime/acceptance-backup.mjs';
import {configureRuntime} from '../../../scripts/runtime/configure.mjs';
import {LocalClient} from '../../../scripts/local-client.mjs';
import {installedContext, withInstalledFixture, withInstallationAdmin} from './fixture.mjs';

test('real backups cover unbound, bound without demo and bound with demo', async () => {
  const projectName = 'webapi-enterprise-local-test-' + randomUUID();
  const directory = path.resolve('.runtime/tests', projectName);
  const ports = await freePorts();
  await withInstalledFixture({directory, projectName, ports, bootstrapUsername:'backup-test-admin'}, async state => {
    async function backup(label) {
      const ctx = await installedContext(directory, state);
      const target = path.join(directory, 'backup-' + label);
      await createBackup(ctx, target);
      const manifest = JSON.parse(await fs.readFile(path.join(target, 'backup-manifest.json'), 'utf8'));
      for (const name of dataVolumes) assert.ok((await fs.stat(path.join(target, name + '.tar'))).size > 0);
      assert.ok(manifest.files['postgres.dump']);
      assert.equal(!!manifest.files['verification-reports.tar'], ctx.reportsSupported);
      if (ctx.reportsSupported) assert.ok((await fs.stat(path.join(target, 'verification-reports.tar'))).size > 0);
      const after = await inspectResources(state);
      for (const name of runtimeServices(state)) {
        const service = after.find(r => r.Kind === 'container' && r.Config?.Labels?.['com.docker.compose.service'] === name &&
          r.Config.Labels['com.docker.compose.oneoff'] !== 'True');
        assert(service?.State.Running, 'Cold backup finally must resume ' + name);
      }
      const result = await operateRuntime('start', state, {directory});
      assert.equal(result.phase, state.binding ? 'Registered' : 'Unconfigured');
    }
    await operateRuntime('stop', state, {directory});
    await backup('unbound-already-stopped');
    const client = new LocalClient('http://127.0.0.1:' + ports.console);
    await withInstallationAdmin(directory, state, client, async passwordFile => {
      const org = await client.request('/organizations', {method:'POST', body:{code:'BACKUP_TEST', name:'Backup test'}});
      const project = await client.request('/organizations/' + org.id + '/projects', {method:'POST', body:{code:'BACKUP_TEST', name:'Backup test'}});
      const environment = await client.request('/projects/' + project.id + '/environments', {method:'POST', body:{code:'BACKUP_TEST', name:'Backup test'}});
      const options = {environmentId:environment.id, allowedOrigins:['http://backend-a:8080', 'http://backend-b:8080'],
        demoEnabled:false, username:state.bootstrapUsername, passwordFile};
      state = await configureRuntime(state, options, {directory});
      await backup('bound-without-demo');
      state = await configureRuntime(state, {...options, demoEnabled:true}, {directory});
      await backup('bound-with-demo');
    });
  });
});

test('unlabelled exact data volume blocks initialization without adoption',async()=>{
 const project='webapi-enterprise-local-test-'+randomUUID(),directory=path.resolve('.runtime/tests/'+project),name=project+'_pg';
 let created=false;
 try{await docker(['volume','create',name]);created=true;const before=JSON.parse((await docker(['volume','inspect',name])).stdout);await assert.rejects(initializeRuntime({directory,projectName:project,bootstrapUsername:'ownership-test-admin'}),/project|owner/i);assert.deepEqual(JSON.parse((await docker(['volume','inspect',name])).stdout),before);assert.equal((await inspectResources({projectName:project})).length,1);await assert.rejects(fs.access(path.join(directory,'secrets')));}
 finally{if(created)await docker(['volume','rm',name]);await fs.rm(directory,{recursive:true,force:true});}
});
