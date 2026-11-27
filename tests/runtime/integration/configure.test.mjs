import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {LocalClient} from '../../../scripts/local-client.mjs';
import {freePorts} from '../../../scripts/runtime/acceptance.mjs';
import {configureRuntime} from '../../../scripts/runtime/configure.mjs';
import {inspectResources} from '../../../scripts/runtime/docker.mjs';
import {loadState} from '../../../scripts/runtime/state.mjs';
import {getRuntimeStatus} from '../../../scripts/runtime/status.mjs';
import {withInstallationAdmin, withInstalledFixture} from './fixture.mjs';

test('real environment binding is repeatable and unknown environment cannot recreate services', async () => {
  const projectName = 'webapi-enterprise-local-test-' + randomUUID();
  const directory = path.resolve('.runtime/tests', projectName);
  const ports = await freePorts();
  await withInstalledFixture({directory, projectName, ports, bootstrapUsername:'binding-test-admin'}, async state => {
    const client = new LocalClient('http://127.0.0.1:' + ports.console);
    await withInstallationAdmin(directory, state, client, async passwordFile => {
      const org = await client.request('/organizations', {method:'POST', body:{code:'BIND_TEST', name:'绑定验收组织'}});
      const project = await client.request('/organizations/' + org.id + '/projects', {method:'POST', body:{code:'BIND_TEST', name:'绑定验收项目'}});
      const environment = await client.request('/projects/' + project.id + '/environments', {method:'POST', body:{code:'BIND_TEST', name:'绑定验收环境'}});
      const options = {environmentId:environment.id, allowedOrigins:['http://backend-a:8080', 'http://backend-b:8080'],
        demoEnabled:true, username:state.bootstrapUsername, passwordFile};
      state = await configureRuntime(state, options, {directory});
      let status = await getRuntimeStatus(state, {directory});
      assert.equal(status.phase, 'Registered');
      assert.equal(status.nodes.length, 2);
      const containerIds = async () => (await inspectResources(state)).filter(r => r.Kind === 'container').map(r => r.Id).sort();
      const before = await containerIds();
      await configureRuntime(state, options, {directory});
      assert.deepEqual(await containerIds(), before);
      await assert.rejects(configureRuntime(state, {...options, environmentId:randomUUID()}, {directory}), /another environment/);
      status = await getRuntimeStatus(await loadState(directory), {directory});
      assert.equal(status.environmentStatus, 'Active');
      assert.ok(status.nodes.every(n => n.fresh && n.currentVersion === 0));
    });
  });
});
