import { transformConfig } from '../../scripts/network-policies/scenario.mjs';
import { mappedRequest, mappedResponse } from '../../scripts/network-policies/evidence.mjs';
import { crc32, deflateSync } from 'node:zlib';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
const revision = 'a'.repeat(40), imageId = 'sha256:' + 'b'.repeat(64);
const sha = v => createHash('sha256').update(v).digest('hex');
const ids = Array.from({ length: 12 }, (_, i) => 'P3-' + String(i + 1).padStart(2, '0'));
// Fabricated validator inputs only. These are never published as runtime evidence.
function unitInput() {
    const ownerId = randomUUID(), projectName = 'webapi-enterprise-local-test-' + randomUUID();
    const common = { sourceRevision: revision, imageId, ownerId, projectName };
    const files = [];
    const add = (path, value) => { files.push({ path, type: 'file', content: Buffer.isBuffer(value) ? value : Buffer.from(typeof value === 'string' ? value : JSON.stringify(value)) }); return path; };
    const state = { ownerId, projectName, ports: { console: 14192, gatewayA: 14196, gatewayB: 14197 } };
    const resources = ['control-plane', 'worker', 'console', 'gateway-a', 'gateway-b', 'prod-gateway-a', 'prod-gateway-b'].map(service => ({ Kind: 'container', Id: randomUUID(), Image: imageId, State: { Running: true }, Config: { Labels: { 'com.docker.compose.project': projectName, 'com.webapi.runtime.owner': ownerId, 'com.docker.compose.service': service } } }));
    const suites = ['domain', 'integration', 'gateway', 'console', 'runtime'].map(name => {
        const log = `WEBAPI_FIXED_SOURCE ${revision}\n${['console', 'runtime'].includes(name) ? '✔ RealUnitInput\nℹ tests 1\nℹ pass 1\nℹ fail 0\nℹ skipped 0' : '  Passed WebApi.Tests.RealUnitInput [1 ms]\nPassed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1'}\nWEBAPI_EXIT 0\n`;
        const actual = { name, sourceRevision: revision, passed: 1, failed: 0, skipped: 0, exitCode: 0, log: add(name + '.log', log), originalLogSha256: sha(log) };
        return { ...actual, artifact: add(name + '.json', actual) };
    });
    const targets = ['a', 'b'].map((nodeName, i) => ({ nodeId: randomUUID(), instanceId: randomUUID(), nodeName, acknowledged: true, configVersion: 4, deploymentSequence: 7, snapshotHash: 'd'.repeat(64) }));
    const raw = {
        'P3-01': { coverage: { suite: 'domain', tests: ['WebApi.Tests.RealUnitInput'] } },
        'P3-02': { statuses: { created: 200, updated: 200, validated: 200, copied: 200, readonly: 403, foreign: 404, stale: 412 }, preview: { beforeWrites: 0, afterWrites: 0, beforeCalls: 0, afterCalls: 0 } },
        'P3-03': { preflight: { status: 204, beforeCalls: 0, afterCalls: 0, allowOrigin: 'https://a.example.test', allowCredentials: 'true' }, deniedOrigin: { status: 403, beforeCalls: 0, afterCalls: 0 }, noOrigin: { status: 200 }, businessOptions: { status: 200 }, coverage: { suite: 'gateway', tests: ['WebApi.Tests.RealUnitInput'] }, authDenied: { status: 401, headers: { 'access-control-allow-origin': 'https://a.example.test' } }, rateDenied: { status: 429, headers: { 'access-control-allow-origin': 'https://a.example.test' } }, upstreamError: { status: 503, headers: { 'access-control-allow-origin': 'https://a.example.test' } } },
        'P3-04': { modes: ['anonymous', 'ApiKey', 'JWT'].map(mode => ({ mode, allowedStatus: 200, deniedStatus: 403, spoofedStatus: 403 })), coverage: { suite: 'gateway', tests: ['WebApi.Tests.RealUnitInput'] } },
        'P3-05': { requestJson: mappedRequest, expectedRequestJson: mappedRequest, responseJson: mappedResponse, expectedResponseJson: mappedResponse, previewJson: mappedRequest, requestHeader: 'qa', responseHeader: 'qa', backendPath: '/__network/echo', backendQuery: 'qa=1', atomicFailure: { status: 422, beforeCalls: 0, afterCalls: 0 }, responseFailure: { status: 502, beforeCalls: 0, afterCalls: 1, text: 'controlled error' }, requestEncodings: ['gzip', 'br'].map(encoding => ({ encoding, status: 200, backendJson: mappedRequest })) },
        'P3-06': { first: { status: 200, beforeCalls: 0, afterCalls: 1, body: '[1]', bodyOperationCount: 1 }, hit: { status: 200, beforeCalls: 1, afterCalls: 1, body: '[1]', bodyOperationCount: 0 }, otherOrigin: { status: 200, beforeCalls: 1, afterCalls: 2 }, encodings: ['gzip', 'br'].map(encoding => ({ encoding, status: 200, expectedBody: '[1]', body: '[1]', maxBodyBytes: 1024, encodedBytes: 30, decodedBytes: 3 })), retry: { status: 200, beforeCalls: 0, afterCalls: 3, bodyOperationCount: 1 }, overflow: { status: 413 }, coverage: { suite: 'gateway', tests: ['WebApi.Tests.RealUnitInput'] } },
        'P3-07': { release: { state: 'Succeeded', targetConfigVersion: 4, deploymentSequence: 7, targets }, nodes: targets.map(t => ({ id: t.nodeId, instanceId: t.instanceId, currentConfigVersion: 4, currentDeploymentSequence: 7 })), acks: targets.map(t => ({ ...t, success: true })), snapshotHash: 'd'.repeat(64), frozenConfig: '{"x":1}', publishedConfig: '{"x":1}', workingConfig: '{"x":2}', oldNodeStatus: 409 },
        'P3-08': { histories: ['2.0', '2.1', '2.2', '2.3'].map(schemaVersion => ({ schemaVersion, originalHash: 'e'.repeat(64), rollbackHash: 'e'.repeat(64), targets, originalConfigVersion: 1, rollbackConfigVersion: 1, originalDeploymentSequence: 1, rollbackDeploymentSequence: 7 })), inFlight: { status: 200, oldMarker: 'old', receivedMarker: 'old' } },
        'P3-09': { sourceEnvironmentId: randomUUID(), targetEnvironmentId: randomUUID(), state: 'Succeeded', sourceOrigins: ['https://a.example.test'], targetOrigins: ['https://b.example.test'], sourceRanges: ['10.0.0.0/8'], targetRanges: ['172.16.0.0/12'], frozenTransformHash: sha(transformConfig), targetTransformHash: sha(transformConfig), missingMappingStatus: 422, foreignStatus: 404, readonlyStatus: 403, approvalCount: 2 },
        'P3-10': { logs: { sourceState: 'Available', data: { items: [{ traceId: '1'.repeat(32), policyDecisions: [{ policyType: 'transform', decision: 'Applied', operationIndex: null, bodyOperationCount: 2 }] }] } }, trace: { sourceState: 'Available', data: { spans: [{ traceId: '1'.repeat(32) }] } }, metrics: { sourceState: 'Available', data: { kpis: [{ metric: 'request_count', value: 1 }] } }, privateCanariesExcluded: true },
        'P3-11': { kind: 'cua', actual: true, width: 1440, actions: ['create', 'edit', 'validate', 'copy', 'nine-bindings', 'json-request-preview', 'json-response-preview', 'exact-null-number', 'readonly', 'revision-412', 'dirty-navigation', 'scope-late', 'release-review', 'promotion-mapping', 'logs-trace'].map(id => ({ id, ...common, passed: true, screenshot: 'qa.png' })) },
        'P3-12': { mainBefore: { head: revision, status: 'dirty', files: { a: 'e'.repeat(64) } }, mainAfter: { head: revision, status: 'dirty', files: { a: 'e'.repeat(64) } }, runtimeBefore: { ownerId: 'original', tables: { a: 'e'.repeat(64) }, files: { a: 'e'.repeat(64) } }, runtimeAfter: { ownerId: 'original', tables: { a: 'e'.repeat(64) }, files: { a: 'e'.repeat(64) } }, package2Paused: true, pendingModelChanges: false, modelCoverage: { suite: 'integration', tests: ['WebApi.Tests.RealUnitInput'] } },
    };
    const promotion = raw['P3-09'];
    promotion.promotionStatus = 'Completed';
    promotion.sourceArtifact = { sourceEnvironmentId: promotion.sourceEnvironmentId, content: { routes: [{ policies: [{ type: 'transform', frozenConfig: transformConfig }] }] } };
    promotion.targetRelease = { state: 'Succeeded', targetConfigVersion: 4, deploymentSequence: 7, frozenCandidate: { policies: [{ type: 'transform', config: transformConfig }] }, targets: targets.map(t => ({ ...t, nodeId: randomUUID(), instanceId: randomUUID() })), approvalSteps: [{ status: 'Approved', actorId: randomUUID() }, { status: 'Approved', actorId: randomUUID() }] };
    promotion.targetNodes = promotion.targetRelease.targets.map(t => ({ id: t.nodeId, instanceId: t.instanceId, environmentId: promotion.targetEnvironmentId, currentConfigVersion: 4, currentDeploymentSequence: 7 }));
    promotion.actualRequests = [{ status: 200, text: mappedResponse.replace('https://a.example.test', 'https://b.example.test') }, { status: 200, text: mappedResponse.replace('https://a.example.test', 'https://b.example.test') }];
    promotion.sourceCredentialDenied = 401;
    promotion.publicEntry = { url: 'https://127.0.0.1:55101', internalUrl: 'http://127.0.0.1:55102', tlsVerified: true, certificateSha256: 'f'.repeat(64), sourceCredentialDenied: 401, request: { status: 200, tlsVerified: true, text: promotion.actualRequests[0].text } };
    promotion.targetRelease.accessContext = { publicOrigin: promotion.publicEntry.url, internalOrigin: promotion.publicEntry.internalUrl };
    const chunk = (type, data) => { const out = Buffer.alloc(data.length + 12); out.writeUInt32BE(data.length); out.write(type, 4); data.copy(out, 8); out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length); return out; };
    const header = Buffer.alloc(13);
    header.writeUInt32BE(1440);
    header.writeUInt32BE(900, 4);
    header[8] = 8;
    header[9] = 6;
    const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.alloc((1440 * 4 + 1) * 900))), chunk('IEND', Buffer.alloc(0))]);
    add('qa.png', png);
    const sections = ids.map(id => ({ id, artifact: add(id + '.json', { ...common, id, actual: true, ...raw[id] }) }));
    const images = add('images.json', { ...common, resources, images: [{ Id: imageId, Config: { Labels: { 'org.opencontainers.image.revision': revision } } }] });
    const proof = { ...common, schemaVersion: 1, stage: 'isolated-pre-install', runtime: { state, directory: '/tmp/' + projectName, initializedEmpty: true, restored: false }, suites, sections, images, artifacts: files.map(f => ({ path: f.path, sha256: sha(f.content) })) };
    return { proof, files };
}
function mutate(f, p, fn) { const x = f.files.find(x => x.path === p), v = JSON.parse(x.content); fn(v); x.content = Buffer.from(JSON.stringify(v)); f.proof.artifacts.find(x => x.path === p).sha256 = sha(x.content); }
async function validate(f, options = {}) { const m = await import('../../scripts/network-policies/evidence.mjs'); return m.validateNetworkPolicyEvidence(f.proof, f.files, { revision, imageId, ...options }); }
test('unit-only complete structured input validates', async () => assert.equal((await validate(unitInput())).passed, true));
for (const id of ids)
    test('missing ' + id + ' blocks acceptance', async () => { const f = unitInput(); f.proof.sections = f.proof.sections.filter(x => x.id !== id); await assert.rejects(validate(f)); });
const corruptions = {
    actual: f => mutate(f, 'P3-05.json', x => x.actual = false),
    source: f => mutate(f, 'P3-05.json', x => x.sourceRevision = 'c'.repeat(40)),
    image: f => mutate(f, 'images.json', x => x.images[0].Config.Labels['org.opencontainers.image.revision'] = 'c'.repeat(40)),
    owner: f => mutate(f, 'P3-07.json', x => x.ownerId = randomUUID()),
    oneAck: f => mutate(f, 'P3-07.json', x => x.release.targets.pop()),
    currentInstance: f => mutate(f, 'P3-07.json', x => x.nodes[0].instanceId = randomUUID()),
    oldHash: f => mutate(f, 'P3-08.json', x => x.histories[0].rollbackHash = 'f'.repeat(64)),
    failed: f => mutate(f, 'gateway.json', x => x.failed = 1),
    skipped: f => mutate(f, 'gateway.json', x => x.skipped = 1),
    secret: f => mutate(f, 'P3-05.json', x => x.secret = 'PRIVATE_CANARY_NETWORK'),
    http: f => mutate(f, 'P3-03.json', x => x.preflight.status = 200),
    calls: f => mutate(f, 'P3-03.json', x => x.preflight.afterCalls = 1),
    mappings: f => mutate(f, 'P3-06.json', x => x.hit.bodyOperationCount = 1),
    json: f => mutate(f, 'P3-05.json', x => x.requestJson = '{"n":9007199254740992}'),
    bytes: f => mutate(f, 'P3-06.json', x => x.encodings[0].decodedBytes = 1025),
    restored: f => f.proof.runtime.restored = true,
    protectedPort: f => f.proof.runtime.state.ports.console = 4192,
    protection: f => mutate(f, 'P3-12.json', x => x.mainAfter.status = 'clean'),
    ui: f => mutate(f, 'P3-11.json', x => x.actions.pop()),
    wrapper: f => { f.proof.sections[0].actual = true; mutate(f, 'P3-01.json', x => x.actual = false); },
    missingCoverage: f => mutate(f, 'P3-04.json', x => x.coverage.tests = ['NoSuchTest']),
};
for (const [name, change] of Object.entries(corruptions))
    test('rejects ' + name, async () => { const f = unitInput(); change(f); await assert.rejects(validate(f, { privateValues: ['PRIVATE_CANARY_NETWORK'] })); });
test('accepts actual management200/request422 and actual NodeDto/GatewayAck fields', async () => { const f = unitInput(); mutate(f, 'P3-02.json', v => { v.statuses.created = 200; v.statuses.copied = 200; }); mutate(f, 'P3-05.json', v => v.atomicFailure.status = 422); mutate(f, 'P3-06.json', v => v.overflow.status = 413); mutate(f, 'P3-07.json', v => { v.acks = v.release.targets.map(t => ({ ...t, success: true })); }); assert.equal((await validate(f)).passed, true); });
test('fixed suite prerequirement refuses missing real logs without creating a runtime', async () => { const m = await import('../../scripts/network-policies/fixtures.mjs'); await assert.rejects(m.validateFixedNetworkSuites('/tmp/no-network-suite-' + randomUUID(), revision)); });
test('header-only fake PNG cannot qualify as 1440px screenshot', async () => { const f = unitInput(), x = f.files.find(x => x.path === 'qa.png'); x.content = Buffer.alloc(33); Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]).copy(x.content); x.content.writeUInt32BE(13, 8); x.content.write('IHDR', 12); x.content.writeUInt32BE(1440, 16); x.content.writeUInt32BE(900, 20); f.proof.artifacts.find(x => x.path === 'qa.png').sha256 = sha(x.content); await assert.rejects(validate(f)); });
test('protection does not collapse composite-key rows or hide one duplicate deletion', async () => { const m = await import('../../scripts/network-policies/fixtures.mjs'), a = 'a'.repeat(64), b = 'b'.repeat(64), c = 'c'.repeat(64); assert.throws(() => m.assertProtectedTableMultisets({ join: { mode: 'append-preserve', rowHashes: [a, b, b] } }, { join: { mode: 'append-preserve', rowHashes: [a, b, c] } })); });
test('protection allows append-only additions while preserving every original duplicate', async () => { const m = await import('../../scripts/network-policies/fixtures.mjs'), a = 'a'.repeat(64), b = 'b'.repeat(64); assert.equal(m.assertProtectedTableMultisets({ join: { mode: 'append-preserve', rowHashes: [a, a] } }, { join: { mode: 'append-preserve', rowHashes: [a, a, b] } }), true); });
for (const problem of ['production image', 'target ACK', 'raw frozen Transform'])
    test('internal ' + problem + ' cannot be replaced by a successful wrapper', async () => { const f = unitInput(); if (problem === 'production image')
        mutate(f, 'images.json', v => v.resources.find(r => r.Config?.Labels?.['com.docker.compose.service'] === 'prod-gateway-a').Image = 'sha256:' + 'c'.repeat(64)); if (problem === 'target ACK')
        mutate(f, 'P3-09.json', v => v.targetRelease.targets[0].acknowledged = false); if (problem === 'raw frozen Transform')
        mutate(f, 'P3-09.json', v => v.targetRelease.frozenCandidate.policies[0].config = '{}'); await assert.rejects(validate(f)); });

for (const problem of ['missing', 'http', 'unverified', 'status', 'body', 'credential', 'release-origin', 'internal-origin', 'certificate'])
    test('production HTTPS entry evidence rejects ' + problem, async () => {
        const f = unitInput();
        mutate(f, 'P3-09.json', v => {
            const entry = v.publicEntry;
            if (problem === 'missing') delete v.publicEntry;
            if (problem === 'http') entry.url = entry.url.replace('https:', 'http:');
            if (problem === 'unverified') entry.request.tlsVerified = false;
            if (problem === 'status') entry.request.status = 502;
            if (problem === 'body') entry.request.text = '{}';
            if (problem === 'credential') entry.sourceCredentialDenied = 200;
            if (problem === 'release-origin') v.targetRelease.accessContext.publicOrigin = 'https://127.0.0.1:55103';
            if (problem === 'internal-origin') v.targetRelease.accessContext.internalOrigin = 'http://127.0.0.1:55103';
            if (problem === 'certificate') entry.certificateSha256 = 'unverified';
        });
        await assert.rejects(validate(f));
    });
