import { crc32, inflateSync } from 'node:zlib';
import assert from 'node:assert/strict';
import { assertPublicFiles, validateCloneOwnership } from '../contracts/evidence.mjs';
import { digest, parseSuiteLog } from '../gateway-policies/evidence.mjs';
import { isPrivateRuntimeArtifact } from '../gateway-policies/protection.mjs';
export { digest };
export const requestSample = '{"n":9007199254740993,"nil":null,"items":[],"copy":"c","drop":true,"move":"m"}';
export const mappedRequest = '{"n":9007199254740993,"nil":null,"items":["mapped"],"copy":"c","copied":"c","moved":"m"}';
export const mappedResponse = JSON.stringify({ method: 'POST', path: '/__network/echo', query: 'qa=1', requestHeader: 'qa', origin: 'https://a.example.test', receivedApiKey: false, body: 'RAW_BODY' }).replace('"RAW_BODY"', mappedRequest.replace('["mapped"]', '["mapped","response"]'));
export const requiredSections = Object.freeze(Array.from({ length: 12 }, (_, i) => 'P3-' + String(i + 1).padStart(2, '0')));
export const requiredUiActions = Object.freeze(['create', 'edit', 'validate', 'copy', 'nine-bindings', 'json-request-preview', 'json-response-preview', 'exact-null-number', 'readonly', 'revision-412', 'dirty-navigation', 'scope-late', 'release-review', 'promotion-mapping', 'logs-trace']);
const hash = /^[a-f0-9]{64}$/, guid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/;
const equal = (a, b, message) => assert.deepEqual(a, b, message);
function calls(value, status, delta) { assert.equal(value.status, status); assert(Number.isSafeInteger(value.beforeCalls) && value.beforeCalls >= 0); assert(Number.isSafeInteger(value.afterCalls)); assert.equal(value.afterCalls - value.beforeCalls, delta); }
function png(bytes, width) {
    assert(Buffer.isBuffer(bytes) && bytes.length >= 57 && bytes.length <= 64 * 1048576);
    assert(bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])));
    const chunks = [];
    let position = 8, height, channels, ended = false;
    while (position < bytes.length) {
        assert(position + 12 <= bytes.length);
        const length = bytes.readUInt32BE(position);
        assert(length <= 64 * 1048576 && position + 12 + length <= bytes.length);
        const type = bytes.toString('ascii', position + 4, position + 8), data = bytes.subarray(position + 8, position + 8 + length);
        assert.equal(crc32(bytes.subarray(position + 4, position + 8 + length)), bytes.readUInt32BE(position + 8 + length), 'PNG CRC invalid');
        if (position === 8) {
            assert.equal(type, 'IHDR');
            assert.equal(length, 13);
            assert.equal(data.readUInt32BE(0), width);
            height = data.readUInt32BE(4);
            assert(height >= 720 && height <= 5000);
            assert.equal(data[8], 8);
            channels = ({ 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 })[data[9]];
            assert(channels);
            assert.equal(data[10], 0);
            assert.equal(data[11], 0);
            assert.equal(data[12], 0);
        }
        else
            assert.notEqual(type, 'IHDR');
        if (type === 'IDAT')
            chunks.push(data);
        if (type === 'IEND') {
            assert.equal(length, 0);
            assert.equal(position + 12, bytes.length);
            ended = true;
        }
        position += length + 12;
    }
    assert(ended && chunks.length > 0);
    const decoded = inflateSync(Buffer.concat(chunks), { maxOutputLength: (width * channels + 1) * height });
    assert.equal(decoded.length, (width * channels + 1) * height);
    for (let row = 0; row < height; row++)
        assert(decoded[row * (width * channels + 1)] <= 4);
}
export function validateNetworkPolicyEvidence(proof, files, { revision, imageId, privateValues = [] } = {}) {
    assert.match(revision ?? '', /^[a-f0-9]{40}$/);
    assert.match(imageId ?? '', /^sha256:[a-f0-9]{64}$/);
    assert.equal(proof.schemaVersion, 1);
    assert.equal(proof.stage, 'isolated-pre-install');
    assert.equal(proof.sourceRevision, revision);
    assert.equal(proof.imageId, imageId);
    assert.match(proof.ownerId ?? '', guid);
    assert.match(proof.projectName ?? '', /^webapi-enterprise-local-test-[a-f0-9-]{36}$/);
    assertPublicFiles(files, { secrets: privateValues });
    assert(files.every(f => !isPrivateRuntimeArtifact(f.path)));
    const index = new Map(files.map(f => [f.path, f]));
    assert.equal(index.size, files.length);
    assert.equal(proof.artifacts?.length, files.length);
    const artifactPaths = new Set();
    for (const a of proof.artifacts) {
        assert.equal(a.path.split('/').some(p => !p || p === '.' || p === '..'), false);
        assert(!a.path.startsWith('/'));
        assert(!artifactPaths.has(a.path));
        artifactPaths.add(a.path);
        assert(index.has(a.path));
        assert.equal(digest(index.get(a.path).content), a.sha256);
        assert(!/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/.test(String(index.get(a.path).content)));
    }
    const read = p => { assert(index.has(p), '缺少材料 ' + p); return JSON.parse(String(index.get(p).content)); };
    const identity = v => { assert.equal(v.sourceRevision, revision, '内部源版本不一致'); assert.equal(v.imageId, imageId, '内部镜像不一致'); assert.equal(v.ownerId, proof.ownerId, '内部owner不一致'); assert.equal(v.projectName, proof.projectName); };
    assert.equal(proof.runtime?.initializedEmpty, true);
    assert.equal(proof.runtime.restored, false);
    assert.equal(proof.runtime.state.ownerId, proof.ownerId);
    assert.equal(proof.runtime.state.projectName, proof.projectName);
    const images = read(proof.images);
    identity(images);
    validateCloneOwnership(proof.runtime.state, images.resources, proof.runtime.directory);
    const services = ['control-plane', 'worker', 'console', 'gateway-a', 'gateway-b', 'prod-gateway-a', 'prod-gateway-b'];
    const apps = images.resources.filter(r => r.Kind === 'container' && services.includes(r.Config?.Labels?.['com.docker.compose.service']));
    assert.equal(apps.length, 7);
    equal(apps.map(r => r.Config.Labels['com.docker.compose.service']).sort(), services.sort());
    for (const app of apps) {
        assert.equal(app.Image, imageId);
        assert.equal(app.State.Running, true);
    }
    assert.equal(images.images.length, 1);
    assert.equal(images.images[0].Id, imageId);
    assert.equal(images.images[0].Config.Labels['org.opencontainers.image.revision'], revision);
    const suiteLogs = new Map();
    assert.equal(proof.suites?.length, 5);
    assert.equal(new Set(proof.suites.map(s => s.name)).size, 5);
    for (const name of ['domain', 'integration', 'gateway', 'console', 'runtime']) {
        const wrapper = proof.suites.find(s => s.name === name);
        assert(wrapper);
        const actual = read(wrapper.artifact);
        for (const key of ['name', 'sourceRevision', 'passed', 'failed', 'skipped', 'exitCode', 'originalLogSha256', 'log'])
            equal(actual[key], wrapper[key]);
        assert.equal(actual.sourceRevision, revision);
        assert.equal(actual.exitCode, 0);
        assert.equal(actual.failed, 0);
        assert.equal(actual.skipped, 0);
        assert(actual.passed > 0);
        assert(index.has(actual.log));
        const text = String(index.get(actual.log).content);
        assert.equal(digest(text), actual.originalLogSha256);
        assert(text.startsWith('WEBAPI_FIXED_SOURCE ' + revision + '\n'));
        assert(text.trimEnd().endsWith('WEBAPI_EXIT 0'));
        equal(parseSuiteLog(name, text), { passed: actual.passed, failed: 0, skipped: 0 });
        suiteLogs.set(name, text);
    }
    const coverage = v => {
        assert(v?.tests?.length > 0 && new Set(v.tests).size === v.tests.length);
        const log = suiteLogs.get(v.suite);
        assert(log);
        for (const name of v.tests) {
            assert(typeof name === 'string' && name.length > 10);
            assert(log.split('\n').some(line => line.trimStart().startsWith('Passed ' + name + ' ') || line.startsWith('✔ ' + name + ' ')), '未找到真实通过记录 ' + name);
        }
    };
    assert.equal(proof.sections?.length, 12);
    equal(proof.sections.map(s => s.id).sort(), [...requiredSections].sort());
    const section = id => { const v = read(proof.sections.find(s => s.id === id).artifact); identity(v); assert.equal(v.id, id); assert.equal(v.actual, true, '未执行的证据 ' + id); return v; };
    coverage(section('P3-01').coverage);
    const management = section('P3-02');
    equal(Object.keys(management.statuses).sort(), ['copied', 'created', 'foreign', 'readonly', 'stale', 'updated', 'validated']);
    for (const k of ['created', 'updated', 'validated', 'copied'])
        assert.equal(management.statuses[k], 200);
    assert.equal(management.statuses.readonly, 403);
    assert([403, 404].includes(management.statuses.foreign));
    assert.equal(management.statuses.stale, 412);
    for (const [before, after] of [['beforeWrites', 'afterWrites'], ['beforeCalls', 'afterCalls']]) {
        assert(Number.isSafeInteger(management.preview[before]));
        assert.equal(management.preview[before], management.preview[after]);
    }
    const cors = section('P3-03');
    calls(cors.preflight, 204, 0);
    assert.match(cors.preflight.allowOrigin, /^https?:\/\//);
    assert.equal(cors.preflight.allowCredentials, 'true');
    calls(cors.deniedOrigin, 403, 0);
    assert.equal(cors.noOrigin.status, 200);
    assert.equal(cors.businessOptions.status, 200);
    coverage(cors.coverage);
    for (const [name, status] of [['authDenied', 401], ['rateDenied', 429], ['upstreamError', 503]]) {
        assert.equal(cors[name].status, status);
        assert.equal(cors[name].headers['access-control-allow-origin'], 'https://a.example.test');
    }
    const ip = section('P3-04');
    equal(ip.modes.map(m => m.mode).sort(), ['ApiKey', 'JWT', 'anonymous']);
    for (const m of ip.modes) {
        assert.equal(m.allowedStatus, 200);
        assert.equal(m.deniedStatus, 403);
        assert.equal(m.spoofedStatus, 403);
    }
    coverage(ip.coverage);
    const transform = section('P3-05');
    assert.equal(transform.requestJson, mappedRequest);
    assert.equal(transform.responseJson, mappedResponse);
    for (const dir of ['Request', 'Response']) {
        const text = transform[dir.toLowerCase() + 'Json'];
        assert.equal(text, transform['expected' + dir + 'Json']);
        JSON.parse(text);
    }
    assert.equal(transform.previewJson, transform.requestJson);
    assert.equal(transform.requestHeader, 'qa');
    assert.equal(transform.responseHeader, 'qa');
    assert.equal(transform.backendPath, '/__network/echo');
    assert.equal(transform.backendQuery, 'qa=1');
    calls(transform.atomicFailure, 422, 0);
    calls(transform.responseFailure, 502, 1);
    assert(!transform.responseFailure.text.includes('PRIVATE_RESPONSE_NETWORK'));
    equal(transform.requestEncodings.map(c => c.encoding).sort(), ['br', 'gzip']);
    for (const c of transform.requestEncodings) {
        assert.equal(c.status, 200);
        assert.equal(c.backendJson, mappedRequest);
    }
    const cache = section('P3-06');
    calls(cache.first, 200, 1);
    calls(cache.hit, 200, 0);
    assert.equal(cache.first.body, cache.hit.body);
    assert.equal(cache.first.bodyOperationCount, 1);
    assert.equal(cache.hit.bodyOperationCount, 0);
    calls(cache.otherOrigin, 200, 1);
    equal(cache.encodings.map(x => x.encoding).sort(), ['br', 'gzip']);
    for (const c of cache.encodings) {
        assert.equal(c.status, 200);
        assert.equal(c.body, c.expectedBody);
        assert(Number.isInteger(c.maxBodyBytes) && c.maxBodyBytes >= 1024 && c.maxBodyBytes <= 1048576);
        for (const k of ['encodedBytes', 'decodedBytes'])
            assert(Number.isInteger(c[k]) && c[k] >= 0 && c[k] <= c.maxBodyBytes);
    }
    calls(cache.retry, 200, 3);
    assert.equal(cache.retry.bodyOperationCount, 1);
    assert.equal(cache.overflow.status, 413);
    coverage(cache.coverage);
    const publish = section('P3-07');
    assert.equal(publish.release.state, 'Succeeded');
    assert.equal(publish.oldNodeStatus, 409);
    assert.notEqual(publish.workingConfig, publish.frozenConfig);
    assert.equal(publish.publishedConfig, publish.frozenConfig);
    assert.match(publish.snapshotHash, hash);
    assert.equal(publish.nodes.length, 2);
    assert.equal(publish.release.targets.length, 2);
    assert.equal(new Set(publish.nodes.map(n => n.instanceId)).size, 2);
    assert.equal(new Set(publish.nodes.map(n => n.id)).size, 2);
    assert.equal(publish.acks.length, 2);
    for (const t of publish.release.targets) {
        assert.equal(t.acknowledged, true);
        const n = publish.nodes.find(n => n.id === t.nodeId), ack = publish.acks.find(a => a.nodeId === t.nodeId && a.instanceId === t.instanceId);
        assert(n && ack);
        assert.match(n.instanceId, guid);
        assert.equal(t.instanceId, n.instanceId);
        assert.equal(n.currentConfigVersion, publish.release.targetConfigVersion);
        assert.equal(n.currentDeploymentSequence, publish.release.deploymentSequence);
        assert.equal(ack.success, true);
        assert.equal(ack.configVersion, publish.release.targetConfigVersion);
        assert.equal(ack.deploymentSequence, publish.release.deploymentSequence);
        assert.equal(ack.snapshotHash, publish.snapshotHash);
    }
    const history = section('P3-08');
    equal(history.histories.map(h => h.schemaVersion).sort(), ['2.0', '2.1', '2.2', '2.3']);
    for (const h of history.histories) {
        assert.match(h.originalHash, hash);
        assert.equal(h.rollbackHash, h.originalHash);
        assert.equal(h.originalConfigVersion, h.rollbackConfigVersion);
        assert(h.rollbackDeploymentSequence > h.originalDeploymentSequence);
        assert.equal(h.targets.length, 2);
        assert(h.targets.every(t => t.acknowledged));
    }
    assert.equal(history.inFlight.status, 200);
    assert.equal(history.inFlight.receivedMarker, history.inFlight.oldMarker);
    const promotion = section('P3-09');
    assert.match(promotion.sourceEnvironmentId, guid);
    assert.match(promotion.targetEnvironmentId, guid);
    assert.notEqual(promotion.sourceEnvironmentId, promotion.targetEnvironmentId);
    assert.equal(promotion.state, 'Succeeded');
    for (const key of ['Origins', 'Ranges']) {
        assert(promotion['source' + key]?.length > 0 && promotion['target' + key]?.length > 0);
        assert.notDeepEqual(promotion['source' + key], promotion['target' + key]);
    }
    assert.match(promotion.frozenTransformHash, hash);
    assert.equal(promotion.frozenTransformHash, promotion.targetTransformHash);
    assert.equal(promotion.missingMappingStatus, 422);
    assert([403, 404].includes(promotion.foreignStatus));
    assert.equal(promotion.readonlyStatus, 403);
    assert.equal(promotion.approvalCount, 2);
    assert.equal(promotion.promotionStatus, 'Completed');
    assert.equal(promotion.targetRelease.state, 'Succeeded');
    assert.equal(promotion.targetRelease.targets.length, 2);
    assert.equal(promotion.targetNodes.length, 2);
    assert.equal(new Set(promotion.targetNodes.map(n => n.id)).size, 2);
    for (const target of promotion.targetRelease.targets) {
        assert.equal(target.acknowledged, true);
        const node = promotion.targetNodes.find(n => n.id === target.nodeId);
        assert(node);
        assert.equal(node.environmentId, promotion.targetEnvironmentId);
        assert.equal(node.instanceId, target.instanceId);
        assert.equal(node.currentConfigVersion, promotion.targetRelease.targetConfigVersion);
        assert.equal(node.currentDeploymentSequence, promotion.targetRelease.deploymentSequence);
        assert(!publish.nodes.some(n => n.id === node.id));
    }
    assert.equal(promotion.sourceArtifact.sourceEnvironmentId, promotion.sourceEnvironmentId);
    const template = promotion.sourceArtifact.content.routes[0].policies.find(p => p.type === 'transform'), targetTransform = promotion.targetRelease.frozenCandidate.policies.find(p => p.type === 'transform');
    assert(template && targetTransform);
    assert.equal(digest(template.frozenConfig), promotion.frozenTransformHash);
    assert.equal(digest(targetTransform.config), promotion.targetTransformHash);
    assert.equal(promotion.actualRequests.length, 2);
    for (const r of promotion.actualRequests) {
        assert.equal(r.status, 200);
        assert.equal(r.text, mappedResponse.replace('https://a.example.test', 'https://b.example.test'));
    }
    assert.equal(promotion.sourceCredentialDenied, 401);
    const entry = promotion.publicEntry;
    assert(entry);
    assert.match(entry.url, /^https:\/\/127\.0\.0\.1:[0-9]{1,5}$/);
    assert.match(entry.internalUrl, /^http:\/\/127\.0\.0\.1:[0-9]{1,5}$/);
    for (const origin of [entry.url, entry.internalUrl])
        assert(Number(new URL(origin).port) > 1024);
    assert.equal(entry.url, promotion.targetRelease.accessContext?.publicOrigin);
    assert.equal(entry.internalUrl, promotion.targetRelease.accessContext?.internalOrigin);
    assert.equal(entry.tlsVerified, true);
    assert.equal(entry.request?.tlsVerified, true);
    assert.equal(entry.request.status, 200);
    assert.equal(entry.request.text, mappedResponse.replace('https://a.example.test', 'https://b.example.test'));
    assert.equal(entry.sourceCredentialDenied, 401);
    assert.match(entry.certificateSha256, hash);
    const approvals = promotion.targetRelease.approvalSteps.filter(a => a.status === 'Approved');
    assert.equal(approvals.length, 2);
    assert.equal(new Set(approvals.map(a => a.actorId)).size, 2);
    const observation = section('P3-10');
    for (const key of ['logs', 'trace', 'metrics'])
        assert.equal(observation[key]?.sourceState, 'Available');
    assert(observation.logs.data.items.length > 0);
    assert(observation.trace.data.spans.length > 0);
    assert(observation.metrics.data.kpis.some(k => k.metric === 'request_count' && Number.isFinite(k.value) && k.value > 0));
    assert(observation.logs.data.items.some(x => x.policyDecisions?.some(p => p.policyType === 'transform' && Number.isInteger(p.bodyOperationCount))));
    assert.equal(observation.privateCanariesExcluded, true);
    const ui = section('P3-11');
    assert.equal(ui.kind, 'cua');
    assert.equal(ui.width, 1440);
    for (const id of requiredUiActions) {
        const action = ui.actions?.find(a => a.id === id);
        assert(action, '缺少CUA操作 ' + id);
        identity(action);
        assert.equal(action.passed, true);
        assert(index.has(action.screenshot));
        png(index.get(action.screenshot).content, 1440);
    }
    const protection = section('P3-12');
    for (const k of ['main', 'runtime']) {
        assert(protection[k + 'Before'] && Object.keys(protection[k + 'Before']).length > 0);
        equal(protection[k + 'Before'], protection[k + 'After']);
    }
    assert.equal(protection.package2Paused, true);
    assert.equal(protection.pendingModelChanges, false);
    coverage(protection.modelCoverage);
    return { passed: true, sourceRevision: revision, imageId, ownerId: proof.ownerId, stage: proof.stage, sections: requiredSections, originalInstallation: 'not-upgraded' };
}
export async function assembleNetworkPolicyEvidence({ revision, imageId, cloneDirectory, fixtureDirectory, directory, suiteDirectory, uiFile, protectionFile }) {
    const fs = (await import('node:fs/promises')).default, path = (await import('node:path')).default;
    const { safeFile, ownedDirectory, loadState, writePrivate } = await import('../runtime/state.mjs');
    const { inspectResources, docker } = await import('../runtime/docker.mjs');
    const { validateFixedNetworkSuites } = await import('./fixtures.mjs');
    await ownedDirectory(directory);
    await ownedDirectory(fixtureDirectory);
    const state = await loadState(cloneDirectory), common = { sourceRevision: revision, imageId, ownerId: state.ownerId, projectName: state.projectName };
    const suites = await validateFixedNetworkSuites(suiteDirectory, revision);
    await safeFile(path.join(fixtureDirectory, 'scenario-result.json'));
    const scenario = JSON.parse(await fs.readFile(path.join(fixtureDirectory, 'scenario-result.json')));
    for (const key of Object.keys(common))
        assert.equal(scenario[key], common[key]);
    assert.equal(scenario.actual, true);
    for (const file of [uiFile, protectionFile])
        await safeFile(file);
    const ui = JSON.parse(await fs.readFile(uiFile)), protection = JSON.parse(await fs.readFile(protectionFile));
    for (const value of [ui, protection])
        for (const key of Object.keys(common))
            assert.equal(value[key], common[key]);
    assert.equal(ui.id, 'P3-11');
    assert.equal(protection.id, 'P3-12');
    const publicDirectory = path.join(directory, 'public');
    await fs.mkdir(publicDirectory, { mode: 0o700 });
    await ownedDirectory(publicDirectory);
    const files = [];
    const add = async (name, content) => { assert(!files.some(f => f.path === name)); const bytes = Buffer.isBuffer(content) ? content : Buffer.from(typeof content === 'string' ? content : JSON.stringify(content, null, 2) + '\n'); files.push({ path: name, type: 'file', content: bytes }); await writePrivate(path.join(publicDirectory, name), bytes.toString('utf8')); };
    // Binary screenshots need a binary write; never stringify PNG bytes.
    const addPng = async (name, file) => { assert(/^[a-z0-9-]+\.png$/.test(name)); await safeFile(file); const bytes = await fs.readFile(file); assert(!files.some(f => f.path === name)); files.push({ path: name, type: 'file', content: bytes }); await fs.writeFile(path.join(publicDirectory, name), bytes, { flag: 'wx', mode: 0o600 }); };
    const sections = [];
    for (const entry of scenario.sections) {
        assert(requiredSections.includes(entry.id) && !['P3-11', 'P3-12'].includes(entry.id));
        assert.equal(entry.artifact, entry.id + '.json');
        await safeFile(path.join(fixtureDirectory, entry.artifact));
        await add(entry.artifact, await fs.readFile(path.join(fixtureDirectory, entry.artifact)));
        sections.push(entry);
    }
    await add('P3-11.json', ui);
    await add('P3-12.json', protection);
    sections.push({ id: 'P3-11', artifact: 'P3-11.json' }, { id: 'P3-12', artifact: 'P3-12.json' });
    for (const screenshot of new Set(ui.actions.map(a => a.screenshot)))
        await addPng(screenshot, path.join(path.dirname(uiFile), screenshot));
    for (const suite of suites.suites) {
        await add(suite.artifact, await fs.readFile(path.join(suiteDirectory, 'public', suite.artifact)));
        await add(suite.log, await fs.readFile(path.join(suiteDirectory, 'public', suite.log)));
    }
    const rawResources = await inspectResources(state), resources = rawResources.map(r => ({ Kind: r.Kind, Id: r.Id, Name: r.Name, Image: r.Image, State: r.State ? { Running: r.State.Running } : undefined, Labels: r.Labels, Config: r.Config ? { Labels: r.Config.Labels } : undefined })), image = JSON.parse((await docker(['image', 'inspect', imageId])).stdout)[0];
    await add('images.json', { ...common, resources, images: [{ Id: image.Id, Config: { Labels: image.Config.Labels } }] });
    const proof = { ...common, schemaVersion: 1, stage: 'isolated-pre-install', runtime: { state, directory: cloneDirectory, initializedEmpty: true, restored: false }, images: 'images.json', sections: sections.sort((a, b) => a.id.localeCompare(b.id)), suites: suites.suites, artifacts: files.map(f => ({ path: f.path, sha256: digest(f.content) })) };
    await safeFile(path.join(fixtureDirectory, 'private-values.json'));
    const privateValues = JSON.parse(await fs.readFile(path.join(fixtureDirectory, 'private-values.json')));
    const result = validateNetworkPolicyEvidence(proof, files, { revision, imageId, privateValues });
    await writePrivate(path.join(directory, 'private-values.json'), privateValues);
    await writePrivate(path.join(directory, 'acceptance-input.json'), proof);
    return result;
}
