import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID, createHash} from 'node:crypto';
import {LocalClient} from '../../../scripts/local-client.mjs';
import {freePorts} from '../../../scripts/runtime/acceptance.mjs';
import {inspectResources} from '../../../scripts/runtime/docker.mjs';
import {initializeRuntime, operateRuntime} from '../../../scripts/runtime/lifecycle.mjs';
import {installedContext, localReleaseFile, withInstalledFixture} from './fixture.mjs';

test('real repeated bootstrap and stop/start preserve administrator, secrets and volumes', async () => {
  const projectName = 'webapi-enterprise-local-test-' + randomUUID();
  const directory = path.resolve('.runtime/tests', projectName);
  const options = {directory, projectName, ports:await freePorts(), bootstrapUsername:'lifecycle-test-admin', releaseFile:localReleaseFile};
  await withInstalledFixture(options, async state => {
    const password = (await fs.readFile(path.join(directory, 'secrets/bootstrap-password'), 'utf8')).trimEnd();
    const client = new LocalClient('http://127.0.0.1:' + options.ports.console);
    await client.login(options.bootstrapUsername, password);
    assert.equal((await client.request('/auth/me')).username, options.bootstrapUsername);
    const nodeSecretHash = async () => createHash('sha256').update(await fs.readFile(path.join(directory, 'secrets/node-a'))).digest('hex');
    const volumeNames = async () => (await inspectResources(state)).filter(r => r.Kind === 'volume').map(r => r.Name).sort();
    const before = await nodeSecretHash();
    const volumes = await volumeNames();
    await initializeRuntime(options);
    assert.equal(await nodeSecretHash(), before);
    await operateRuntime('stop', state, {directory});
    await operateRuntime('start', state, {directory});
    assert.equal((await client.request('/auth/me')).username, options.bootstrapUsername);
    assert.deepEqual(await volumeNames(), volumes);
    const ctx = await installedContext(directory, state);
    const count = await ctx.compose('exec', '-T', 'postgres', 'psql', '-U', 'webapi', '-d', 'webapi', '-At', '-c',
      "select count(*) from users where username='lifecycle-test-admin'");
    assert.equal(count.stdout.trim(), '1');
  });
});
