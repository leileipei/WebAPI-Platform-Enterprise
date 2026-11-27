import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {prepareAcceptanceContext} from '../../scripts/runtime/acceptance.mjs';

async function fixture(t) {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-acceptance-context-'));
    t.after(() => fs.rm(directory, {recursive: true, force: true}));
    const release = {sourceRevision: 'a'.repeat(40), imageId: 'sha256:' + 'b'.repeat(64)};
    const deploy = path.join(directory, 'artifacts', release.sourceRevision, 'deploy');
    await fs.mkdir(deploy, {recursive: true});
    await fs.writeFile(path.join(deploy, 'compose.runtime.yml'), JSON.stringify({services: {'control-plane': {}}}));
    const state = {
        projectName: 'webapi-enterprise-local-test-11111111-1111-4111-8111-111111111111',
        ownerId: '22222222-2222-4222-8222-222222222222',
        ports: {console: 14192, gatewayA: 14196, gatewayB: 14197},
        initialized: true
    };
    return {directory, release, state};
}

test('first gateway startup uses acceptance telemetry settings while environment binding is pending', async t => {
    const {directory, release, state} = await fixture(t);
    const binding = {environmentId: '33333333-3333-4333-8333-333333333333', allowedOrigins: ['http://backend-a:8080']};
    const ctx = await prepareAcceptanceContext(directory, {...state, pendingConfiguration: {binding, demoEnabled: true}}, release);
    assert.ok(ctx.files.some(file => file.endsWith('/compose.runtime.gateways.yml')));
    assert.equal(ctx.env.WEBAPI_RUNTIME_ENVIRONMENT_ID, binding.environmentId);
    const override = JSON.parse(await fs.readFile(ctx.files.at(-1), 'utf8'));
    for (const node of ['gateway-a', 'gateway-b']) {
        assert.deepEqual(override.services[node]?.environment, {
            Observability__TraceSampleRatio: '1',
            Observability__MetricExportIntervalMs: '1000'
        }, node + ' must export at the acceptance interval from startup');
    }
});

test('acceptance initialization does not add gateway services before an environment is selected', async t => {
    const {directory, release, state} = await fixture(t);
    const ctx = await prepareAcceptanceContext(directory, state, release);
    const override = JSON.parse(await fs.readFile(ctx.files.at(-1), 'utf8'));
    assert.deepEqual(Object.keys(override.services).sort(), ['control-plane', 'worker']);
    for (const service of ['control-plane', 'worker']) {
        assert.equal(override.services[service].environment.Alerts__IntervalSeconds, '2');
        assert.equal(override.services[service].environment.Alerts__QueryDelaySeconds, '5');
    }
});
