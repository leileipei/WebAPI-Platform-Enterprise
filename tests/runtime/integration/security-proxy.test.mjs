import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID, createHash} from 'node:crypto';
import {operateRuntime} from '../../../scripts/runtime/lifecycle.mjs';
import {freePorts} from '../../../scripts/runtime/acceptance.mjs';
import {getRuntimeStatus} from '../../../scripts/runtime/status.mjs';
import {resolveOwnedConsoleProxy, synchronizeConsoleProxyTrust} from '../../../scripts/security/proxy-trust.mjs';
import {installedContext, localReleaseFile, withInstalledFixture} from './fixture.mjs';

test('actual immutable Console recreation requires exact proxy registration without secret rotation', async () => {
  const projectName = 'webapi-enterprise-local-test-' + randomUUID();
  const directory = path.resolve('.runtime/tests', projectName);
  const releaseFile = path.resolve(process.env.WEBAPI_SECURITY_RELEASE_FILE ?? localReleaseFile);
  const options = {directory, projectName, ports:await freePorts(), bootstrapUsername:'security-proxy-fixture', releaseFile};
  await withInstalledFixture(options, async state => {
    const ctx = await installedContext(directory, state);
    assert.equal(ctx.securitySupported, true);
    const file = path.join(directory, 'secrets/login-protection-hmac');
    const secretHash = async () => createHash('sha256').update(await fs.readFile(file)).digest('hex');
    const hash = await secretHash();
    const original = await resolveOwnedConsoleProxy(state);
    const protection = async () => (await getRuntimeStatus(state, {directory})).loginProtection.state;
    assert.equal(await protection(), 'Ready');
    await ctx.compose('up', '-d', '--no-deps', '--force-recreate', 'console');
    const replaced = await resolveOwnedConsoleProxy(state);
    assert.notEqual(replaced.containerId, original.containerId);
    assert.notEqual(await protection(), 'Ready');
    await synchronizeConsoleProxyTrust(ctx);
    assert.equal(await protection(), 'Ready');
    await operateRuntime('restart', state, {directory});
    assert.equal(await protection(), 'Ready');
    assert.equal(await secretHash(), hash);
  });
});
