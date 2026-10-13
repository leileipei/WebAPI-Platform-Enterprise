import { gzipSync, brotliCompressSync } from 'node:zlib';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import http from 'node:http';
import { randomUUID, randomBytes } from 'node:crypto';
import { loadState, writePrivate, safeFile } from '../runtime/state.mjs';
import { loadRelease } from '../runtime/lifecycle.mjs';
import { prepareContext } from '../runtime/context.mjs';
import { docker, inspectResources } from '../runtime/docker.mjs';
import { prepareScenario, until } from '../runtime/acceptance-scenario.mjs';
import { ContractClient } from '../contracts/scenario.mjs';
import { prepareNetworkBackend, assertNetworkOwners, validateFixedNetworkSuites, assertFixedNetworkSources } from './fixtures.mjs';
import { digest, requestSample, mappedRequest } from './evidence.mjs';
export { requestSample, mappedRequest };
// Numeric operation tokens remain raw JSON. Never round-trip them through JS Number.
export const transformConfig = `{"requestHeaders":[{"action":"Set","name":"X-Network-QA","value":"qa"}],"responseHeaders":[{"action":"Set","name":"X-Network-QA","value":"qa"}],"path":{"removePrefix":"/network","addPrefix":"/__network"},"query":[{"action":"Remove","name":"omit"},{"action":"Set","name":"qa","value":"1"}],"requestBody":{"methods":["POST"],"maxBodyBytes":1024,"operations":[{"op":"add","path":"/items/-","value":"mapped"},{"op":"replace","path":"/n","value":9007199254740993},{"op":"remove","path":"/drop"},{"op":"copy","from":"/copy","path":"/copied"},{"op":"move","from":"/move","path":"/moved"}]},"responseBody":{"statusCodes":[200],"maxBodyBytes":1024,"operations":[{"op":"add","path":"/body/items/-","value":"response"}]}}`;
export async function networkRequest({ port, url, method = 'GET', headers = {}, body, timeoutMs = 30000 }) {
    return new Promise((resolve, reject) => {
        const traceId = randomBytes(16).toString('hex'), bytes = body === undefined ? null : Buffer.from(body);
        const req = http.request({ hostname: '127.0.0.1', port, path: url, method, headers: { Host: 'gateway.fixture.test', traceparent: '00-' + traceId + '-0123456789abcdef-01', ...(bytes ? { 'Content-Length': bytes.length } : {}), ...headers }, signal: AbortSignal.timeout(timeoutMs) }, res => {
            const chunks = [];
            let length = 0;
            res.on('data', chunk => {
                length += chunk.length;
                if (length > 2 * 1048576) {
                    res.destroy(Error('Diagnostic response budget exceeded'));
                    return;
                }
                chunks.push(chunk);
            });
            res.on('error', reject);
            res.on('end', () => { const buffer = Buffer.concat(chunks); resolve({ status: res.statusCode, headers: res.headers, text: buffer.toString('utf8'), bytes: buffer.length, traceId }); });
        });
        req.on('error', reject);
        req.end(bytes);
    });
}
// Reuses ContractClient's cookies/CSRF. Capture real success status rather than inferring it from a DTO.
async function management(client, route, options = {}) {
    const headers = { 'Content-Type': 'application/json', Origin: client.base, ...options.headers };
    if (options.method && options.method !== 'GET') {
        headers['X-CSRF-Token'] = (await client.request('/auth/csrf')).token;
        headers['Idempotency-Key'] = options.key ?? randomUUID();
    }
    headers.Cookie = [...client.cookies].map(([k, v]) => k + '=' + v).join('; ');
    const r = await fetch(client.base + '/api/v1' + route, { ...options, headers, body: options.body === undefined ? undefined : JSON.stringify(options.body), signal: AbortSignal.timeout(30000) });
    return { status: r.status, value: r.status === 204 ? null : await r.json() };
}
export async function runNetworkPolicyScenario({ revision, imageId, cloneDirectory, fixtureDirectory }) {
    await assertFixedNetworkSources(revision);
    let state = await assertNetworkOwners({ cloneDirectory });
    const release = await loadRelease(path.join(cloneDirectory, 'release.json'));
    assert.equal(release.sourceRevision, revision);
    assert.equal(release.imageId, imageId);
    await safeFile(path.join(cloneDirectory, 'empty-origin.json'));
    const empty = JSON.parse(await fs.readFile(path.join(cloneDirectory, 'empty-origin.json')));
    assert.equal(empty.ownerId, state.ownerId);
    assert.equal(empty.initializedEmpty, true);
    assert.equal(empty.restored, false);
    await safeFile(path.join(cloneDirectory, 'fixed-suites-pointer.json'));
    const suitePointer = JSON.parse(await fs.readFile(path.join(cloneDirectory, 'fixed-suites-pointer.json')));
    const fixedSuites = await validateFixedNetworkSuites(suitePointer.directory, revision);
    const backend = await prepareNetworkBackend({ revision, imageId, cloneDirectory, fixtureDirectory });
    const contextFactory = (directory, current) => prepareContext(directory, current, release);
    const s = await prepareScenario(cloneDirectory, state, contextFactory, { fixtureAllowedOrigins: ['http://backend-a:8080', 'http://backend-b:8080', backend.backendOrigin, backend.backendOriginB] });
    state = await loadState(cloneDirectory);
    const ctx = await contextFactory(cloneDirectory, state), admin = new ContractClient(s.endpoint);
    await admin.login(state.bootstrapUsername, s.password);
    const overlayFile = path.join(cloneDirectory, 'runtime-config.json'), overlay = JSON.parse(await fs.readFile(overlayFile));
    for (const name of ['control-plane', 'worker', 'gateway-a', 'gateway-b']) {
        overlay.services[name].environment.GatewayPolicies__FixtureEnabled = 'true';
        overlay.services[name].environment.GatewayPolicies__Jwt__HttpFixtureOrigins__0 = new URL(backend.issuer).origin;
        if (name.startsWith('gateway')) {
            overlay.services[name].environment.Observability__TraceSampleRatio = '1';
            overlay.services[name].environment.GatewayPolicies__Transform__MaxConcurrentBodyTransforms = '1';
        }
    }
    await writePrivate(overlayFile, overlay);
    await ctx.compose('up', '-d', '--force-recreate', 'control-plane', 'worker', 'gateway-a', 'gateway-b');
    await until(() => admin.login(state.bootstrapUsername, s.password), () => true, 90);
    const common = { sourceRevision: revision, imageId, ownerId: state.ownerId, projectName: state.projectName }, sections = [];
    const record = async (id, value) => { await writePrivate(path.join(fixtureDirectory, id + '.json'), { ...common, id, actual: true, ...value }); sections.push({ id, artifact: id + '.json' }); console.log('Observed ' + id); };
    const coverage = (suite, pattern) => { const tests = fixedSuites.logs[suite].split('\n').filter(line => line.trimStart().startsWith('Passed WebApi.') && pattern.test(line)).map(line => line.trimStart().slice(7).replace(/ \[.*$/, '')); assert(tests.length > 0); return { suite, tests: [...new Set(tests)] }; };
    await record('P3-01', { coverage: coverage('domain', /CorsPolicyConfigurationTests|IpFilterPolicyConfigurationTests|TransformPolicyConfigurationTests|JsonBodyMapperTests|JsonBodyConfigurationTests|NetworkPolicySnapshotTests|NetworkPolicyArtifactTests/) });
    const tag = randomUUID().slice(0, 8), scope = s.scope, version = await admin.request('/apis/' + s.api.id + '/versions', { method: 'POST', body: { version: '99.0.0' } }), cluster = await admin.request('/environments/' + scope.environmentId + '/clusters', { method: 'POST', body: { name: '网络策略真实回显 ' + tag, loadBalancingPolicy: 'RoundRobin', healthCheckEnabled: false } });
    await admin.request('/clusters/' + cluster.id + '/destinations', { method: 'POST', body: { name: 'network-echo', address: backend.backendOrigin + '/', weight: 1, enabled: true } });
    const route = await admin.request('/environments/' + scope.environmentId + '/routes', { method: 'POST', body: { apiVersionId: version.id, routeName: 'Network-' + tag, path: '/network/{**path}', methods: ['GET', 'POST', 'HEAD', 'OPTIONS'], clusterId: cluster.id, priority: 100, enabled: true, timeoutMs: 60000, requireApiKey: true } });
    const apiKey = (await fs.readFile(path.join(cloneDirectory, 'credential'), 'utf8')).trim();
    const originA = 'https://a.example.test', originB = 'https://b.example.test', baseHeaders = { 'X-API-Key': apiKey, Origin: originA };
    const backendUrl = 'http://127.0.0.1:' + backend.ports.backend;
    const count = async (url) => (await (await fetch(backendUrl + '/__fixture/stats')).json())[url] ?? 0;
    const configure = async (failures = 0, cacheable = true) => { const r = await fetch(backendUrl + '/__fixture/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ failures, cacheable }) }); assert.equal(r.status, 200); };
    const requests = [];
    const call = async (node, url, options = {}) => { const result = await networkRequest({ port: node ? state.ports.gatewayB : state.ports.gatewayA, url, headers: baseHeaders, ...options }); requests.push({ node, url, method: options.method ?? 'GET', ...result }); return result; };
    const expectedCalls = async (node, url, key, options = {}) => { const beforeCalls = await count(key), r = await call(node, url, options), afterCalls = await count(key); return { ...r, beforeCalls, afterCalls }; };
    const policies = new Map();
    const create = async (type, config) => { const r = await management(admin, '/projects/' + scope.projectId + '/policies', { method: 'POST', body: { name: 'Network ' + tag + ' ' + type, type, config: typeof config === 'string' ? config : JSON.stringify(config), enabled: true } }); assert.equal(r.status, 200); policies.set(type, r.value); return r; };
    const save = async (type, config) => { const p = policies.get(type), r = await management(admin, '/policies/' + p.id, { method: 'PUT', headers: { 'If-Match': '"' + p.versionNo + '"' }, body: { name: p.name, type, config: typeof config === 'string' ? config : JSON.stringify(config), enabled: true } }); assert.equal(r.status, 200); policies.set(type, r.value); return r; };
    const bind = async (types) => { const current = await admin.request('/routes/' + route.id + '/policies'); return admin.request('/routes/' + route.id + '/policies', { method: 'PUT', headers: { 'If-Match': '"' + current.revision + '"' }, body: { bindings: types.map((type, i) => ({ policyId: policies.get(type).id, priority: i + 1 })) } }); };
    const activeTypes = () => [...policies.keys()];
    const makeRelease = async () => { const env = await admin.request('/environments/' + scope.environmentId), selection = { baseConfigVersion: env.desiredConfigVersion, versionIds: [version.id] }, candidate = await admin.request('/environments/' + scope.environmentId + '/releases/preview', { method: 'POST', body: selection }); return admin.request('/environments/' + scope.environmentId + '/releases', { method: 'POST', body: { ...selection, resourceRevisions: candidate.resourceRevisions } }); };
    const approve = async (r) => {
        await admin.request('/releases/' + r.id + '/submit', { method: 'POST' });
        for (const reviewer of s.reviewers)
            await reviewer.request('/releases/' + r.id + '/approve', { method: 'POST', body: { comment: '独立网络策略真实验收' } });
        return admin.request('/releases/' + r.id);
    };
    const done = async (id) => until(() => admin.request('/releases/' + id), r => {
        if (r.state === 'Failed')
            throw Object.assign(Error('Own release failed'), { fatal: true });
        return r.state === 'Succeeded' && r.targets.length === 2 && r.targets.every(t => t.acknowledged);
    }, 120);
    let latest;
    const publish = async () => { const r = await approve(await makeRelease()); await admin.request('/releases/' + r.id + '/publish', { method: 'POST' }); return latest = await done(r.id); };
    const sql = async (q) => (await ctx.compose('exec', '-T', 'postgres', 'psql', '-U', 'webapi', '-d', 'webapi', '-At', '-v', 'ON_ERROR_STOP=1', '-c', q)).stdout.trim();
    const payload = async (n) => { assert(Number.isSafeInteger(n) && n > 0); return Buffer.from((await sql(`SELECT encode(s.payload_bytes,'base64') FROM gateway_config_snapshots s JOIN gateway_config_versions v ON v.id=s.config_version_id WHERE v.environment_id='${scope.environmentId}' AND v.version_no=${n}`)).replaceAll('\n', ''), 'base64'); };
    const histories = [];
    const remember = async (r) => { const bytes = await payload(r.targetConfigVersion), snapshot = JSON.parse(bytes); histories.push({ release: r, bytes, schemaVersion: snapshot.schemaVersion }); return snapshot; };
    await remember(await publish());
    assert.equal(histories.at(-1).schemaVersion, '2.0');
    const configs = { timeout: { timeoutMs: 60000 }, rate_limit: { algorithm: 'TokenBucket', keyBy: 'ApplicationRoute', refillTokens: 1000, windowMs: 1000, burst: 1500, redisFailureMode: 'Allow' }, circuit_breaker: { samplingWindowMs: 30000, minimumRequests: 100, failureRatio: .5, openDurationMs: 1000, halfOpenMaxRequests: 1, halfOpenSuccesses: 2, failureStatusCodes: [503], countTimeouts: true, countConnectionFailures: true }, authentication: { mode: 'ApiKey' }, retry: { maxAttempts: 3, perAttemptTimeoutMs: 2000, baseDelayMs: 10, maxDelayMs: 20, jitterPercent: 0, retryStatusCodes: [502, 503, 504], retryConnectionFailures: true }, cache: { ttlSeconds: 120, maxEntryBytes: 262144, varyHeaders: ['Accept'], identityPartition: 'VerifiedIdentity', redisFailureMode: 'Bypass' } };
    for (const type of ['timeout', 'rate_limit', 'circuit_breaker'])
        await create(type, configs[type]);
    await bind(activeTypes());
    await remember(await publish());
    assert.equal(histories.at(-1).schemaVersion, '2.1');
    for (const type of ['authentication', 'retry', 'cache'])
        await create(type, configs[type]);
    await bind(activeTypes());
    await remember(await publish());
    assert.equal(histories.at(-1).schemaVersion, '2.2');
    const corsConfig = { allowedOrigins: [originA, originB], allowCredentials: true, allowedMethods: ['GET', 'POST', 'OPTIONS'], allowedHeaders: ['X-API-Key', 'Content-Type', 'X-Network-QA'], exposedHeaders: ['X-Network-QA'], maxAgeSeconds: 600 }, ipConfig = { mode: 'AllowList', ranges: ['0.0.0.0/0', '::/0'] };
    const created = await create('cors', corsConfig);
    await create('ip_filter', ipConfig);
    await create('transform', transformConfig);
    await bind(activeTypes());
    await remember(await publish());
    assert.equal(histories.at(-1).schemaVersion, '2.3');
    const updated = await save('cors', corsConfig), validated = await management(admin, '/policies/validate', { method: 'POST', body: { scope: { organizationId: scope.organizationId, projectId: scope.projectId }, type: 'transform', config: transformConfig } }), copied = await management(admin, '/policies/' + policies.get('transform').id + '/copy', { method: 'POST', body: { name: 'Network copy ' + tag, targetProjectId: null } });
    assert.equal(validated.status, 200);
    assert.equal(copied.status, 200);
    const stale = await management(admin, '/policies/' + created.value.id, { method: 'PUT', headers: { 'If-Match': '"' + created.value.versionNo + '"' }, body: { name: created.value.name, type: 'cors', config: JSON.stringify(corsConfig), enabled: true } });
    assert.equal(stale.status, 412);
    const roles = await admin.request('/roles');
    const readonlyPassword = randomBytes(32).toString('base64url');
    let readonlyUser = await admin.request('/users', { method: 'POST', body: { username: 'network-read-' + tag, displayName: 'Network readonly', password: readonlyPassword } });
    readonlyUser = await admin.request('/users/' + readonlyUser.id + '/roles', { method: 'PUT', headers: { 'If-Match': '"' + readonlyUser.revision + '"' }, body: { roleIds: [roles.find(r => r.isSystem && r.code === 'PlatformAdmin').id] } });
    await admin.request('/users/' + readonlyUser.id + '/scopes', { method: 'PUT', headers: { 'If-Match': '"' + readonlyUser.revision + '"' }, body: { scopes: [{ scope: { organizationId: scope.organizationId }, accessMode: 'read' }] } });
    const readonly = new ContractClient(s.endpoint);
    await readonly.login(readonlyUser.username, readonlyPassword);
    const denied = await management(readonly, '/policies/' + policies.get('transform').id, { method: 'PUT', headers: { 'If-Match': '"' + policies.get('transform').versionNo + '"' }, body: { name: 'denied', type: 'transform', config: transformConfig, enabled: true } });
    assert.equal(denied.status, 403);
    const foreign = await admin.request('/organizations', { method: 'POST', body: { code: 'NETWORK_FOREIGN_' + tag, name: '未授权独立组织' } });
    const foreignRead = await management(readonly, '/organizations/' + foreign.id + '/policies');
    assert([403, 404].includes(foreignRead.status));
    const beforeWrites = Number(await sql('SELECT count(*) FROM audit_logs')), beforeCalls = await count('/__network/echo?qa=1');
    const preview = await admin.request('/policies/transform/preview', { method: 'POST', body: { scope: { organizationId: scope.organizationId, projectId: scope.projectId }, config: transformConfig, direction: 'request', sampleJson: requestSample } });
    assert.equal(preview.actualForwarding, false);
    assert.equal(preview.outputJson, mappedRequest);
    const afterWrites = Number(await sql('SELECT count(*) FROM audit_logs')), afterCalls = await count('/__network/echo?qa=1');
    await record('P3-02', { statuses: { created: created.status, updated: updated.status, validated: validated.status, copied: copied.status, readonly: denied.status, foreign: foreignRead.status, stale: stale.status }, preview: { beforeWrites, afterWrites, beforeCalls, afterCalls } });
    const preflight = await expectedCalls(0, '/network/echo', '/__network/echo?qa=1', { method: 'OPTIONS', headers: { Origin: originA, 'Access-Control-Request-Method': 'GET', 'Access-Control-Request-Headers': 'X-API-Key' } });
    assert.equal(preflight.status, 204);
    assert.equal(preflight.beforeCalls, preflight.afterCalls);
    const deniedOrigin = await expectedCalls(1, '/network/echo', '/__network/echo?qa=1', { headers: { ...baseHeaders, Origin: 'https://denied.example.test' } });
    assert.equal(deniedOrigin.status, 403);
    const noOrigin = await call(0, '/network/echo/no-origin', { headers: { 'X-API-Key': apiKey } }), businessOptions = await call(1, '/network/echo/business-options', { method: 'OPTIONS' });
    const authDenied = await call(0, '/network/echo/auth-denied', { headers: { Origin: originA } });
    assert.equal(authDenied.status, 401);
    await configure(100, false);
    const upstreamError = await call(1, '/network/echo/upstream-error');
    assert.equal(upstreamError.status, 503);
    await configure();
    await save('rate_limit', { ...configs.rate_limit, refillTokens: 1, windowMs: 600000, burst: 1 });
    await publish();
    assert.equal((await call(0, '/network/echo/rate-once')).status, 200);
    const rateDenied = await call(1, '/network/echo/rate-once');
    assert.equal(rateDenied.status, 429);
    for (const r of [authDenied, upstreamError, rateDenied])
        assert.equal(r.headers['access-control-allow-origin'], originA);
    await save('rate_limit', configs.rate_limit);
    await publish();
    await record('P3-03', { authDenied, upstreamError, rateDenied, preflight: { ...preflight, allowOrigin: preflight.headers['access-control-allow-origin'], allowCredentials: preflight.headers['access-control-allow-credentials'] }, deniedOrigin, noOrigin, businessOptions, coverage: coverage('gateway', /CorsPipelineTests/) });
    const post = await expectedCalls(0, '/network/echo?omit=1', '/__network/echo?qa=1', { method: 'POST', headers: { ...baseHeaders, 'Content-Type': 'application/json' }, body: requestSample });
    assert.equal(post.status, 200);
    const output = JSON.parse(post.text);
    assert.equal(output.requestHeader, 'qa');
    assert.equal(output.path, '/__network/echo');
    assert.equal(output.query, 'qa=1');
    assert(post.text.includes('9007199254740993'));
    const requestBodyResponse = await fetch(backendUrl + '/__fixture/network/body?' + new URLSearchParams({ path: '/__network/echo?qa=1' }));
    assert.equal(requestBodyResponse.status, 200);
    const requestJson = await requestBodyResponse.text();
    assert.equal(requestJson, mappedRequest);
    const expectedResponseJson = JSON.stringify({ method: 'POST', path: '/__network/echo', query: 'qa=1', requestHeader: 'qa', origin: originA, receivedApiKey: false, body: 'RAW_BODY' }).replace('"RAW_BODY"', mappedRequest.replace('["mapped"]', '["mapped","response"]'));
    assert.equal(post.text, expectedResponseJson);
    const atomicFailure = await expectedCalls(0, '/network/echo/atomic', '/__network/echo/atomic?qa=1', { method: 'POST', headers: { ...baseHeaders, 'Content-Type': 'application/json' }, body: '{"items":[],"n":1,"private":"PRIVATE_REQUEST_NETWORK"}' });
    assert.equal(atomicFailure.status, 422);
    const requestEncodings = [];
    for (const [encoding, compress] of [['gzip', gzipSync], ['br', brotliCompressSync]]) {
        const key = '/__network/echo/request-' + encoding + '?qa=1', r = await call(1, '/network/echo/request-' + encoding, { method: 'POST', headers: { ...baseHeaders, 'Content-Type': 'application/json', 'Content-Encoding': encoding }, body: compress(Buffer.from(requestSample)) });
        assert.equal(r.status, 200);
        const raw = await (await fetch(backendUrl + '/__fixture/network/body?' + new URLSearchParams({ path: key }))).text();
        assert.equal(raw, mappedRequest);
        requestEncodings.push({ encoding, status: r.status, backendJson: raw });
    }
    const responseFailure = await expectedCalls(0, '/network/echo/invalid', '/__network/echo/invalid?qa=1');
    assert.equal(responseFailure.status, 502);
    assert.equal(responseFailure.afterCalls - responseFailure.beforeCalls, 1);
    assert(!responseFailure.text.includes('PRIVATE_RESPONSE_NETWORK'));
    await record('P3-05', { requestEncodings, responseFailure, requestJson, expectedRequestJson: mappedRequest, responseJson: post.text, expectedResponseJson, previewJson: preview.outputJson, requestHeader: output.requestHeader, responseHeader: post.headers['x-network-qa'], backendPath: output.path, backendQuery: output.query, atomicFailure });
    const cacheUrl = '/network/echo/cache-' + tag, cacheKey = '/__network/echo/cache-' + tag + '?qa=1';
    await configure();
    const first = await expectedCalls(0, cacheUrl, cacheKey);
    assert.equal(first.status, 200);
    await until(() => logs(first.traceId), v => v.data?.items?.some(x => x.traceId === first.traceId && x.cacheDisposition === 'Stored'), 90);
    const hit = await expectedCalls(1, cacheUrl, cacheKey);
    assert.equal(hit.afterCalls, hit.beforeCalls);
    assert.equal(hit.text, first.text);
    const otherOrigin = await expectedCalls(1, cacheUrl, cacheKey, { headers: { ...baseHeaders, Origin: originB } });
    assert.equal(otherOrigin.afterCalls, otherOrigin.beforeCalls + 1);
    const encodings = [];
    for (const encoding of ['gzip', 'br']) {
        const r = await call(0, '/network/echo/' + encoding);
        assert.equal(r.status, 200);
        assert.equal(r.headers['content-encoding'], undefined);
        assert(r.text.includes('9007199254740993'));
        encodings.push({ encoding, status: r.status, body: r.text, expectedBody: JSON.stringify({ method: 'GET', path: '/__network/echo/' + encoding, query: 'qa=1', requestHeader: 'qa', origin: originA, receivedApiKey: false, body: 'RAW_BODY' }).replace('"RAW_BODY"', '{"n":9007199254740993,"nil":null,"items":["upstream","response"]}'), maxBodyBytes: 1024, encodedBytes: Number(r.headers['x-fixture-encoded-bytes']), decodedBytes: Number(r.headers['x-fixture-decoded-bytes']) });
    }
    await configure(2, false);
    const retry = await expectedCalls(0, '/network/echo/retry-' + tag, '/__network/echo/retry-' + tag + '?qa=1');
    assert.equal(retry.afterCalls - retry.beforeCalls, 3);
    await configure();
    const overflow = await call(0, '/network/echo', { method: 'POST', headers: { ...baseHeaders, 'Content-Type': 'application/json' }, body: '{"long":"' + 'x'.repeat(1100) + '"}' });
    assert.equal(overflow.status, 413);
    const range = () => ({ ...scope, start: new Date(Date.now() - 600000).toISOString(), end: new Date().toISOString() });
    async function logs(traceId) { return admin.request('/observability/logs?' + new URLSearchParams({ ...range(), traceId })); }
    const decisions = async (r) => (await until(() => logs(r.traceId), v => v.data?.items?.some(x => x.traceId === r.traceId), 90)).data.items.find(x => x.traceId === r.traceId).policyDecisions;
    const bodyCount = async (r) => (await decisions(r)).find(p => p.policyType === 'transform')?.bodyOperationCount;
    await record('P3-06', { first: { ...first, body: first.text, bodyOperationCount: await bodyCount(first) }, hit: { ...hit, body: hit.text, bodyOperationCount: await bodyCount(hit) }, otherOrigin, encodings, retry: { ...retry, bodyOperationCount: await bodyCount(retry) }, overflow, coverage: coverage('gateway', /Json(Request|Response)BodyPipelineTests|BodyTransformBudgetTests|NetworkPolicyCacheTests|NetworkPolicyRetryTests|JsonBodyCodecTests/) });
    const claims = JSON.parse(Buffer.from(backend.tokens.a.split('.')[1], 'base64url')), jwt = { mode: 'JWT', issuer: backend.issuer, audiences: Array.isArray(claims.aud) ? claims.aud : [claims.aud], allowedAlgorithms: ['RS256'], allowedTokenTypes: ['JWT'], clockSkewSeconds: 30, maxTokenLifetimeSeconds: 3600, applicationClaim: 'azp', applicationMappings: [{ claimValue: 'gateway-qa', applicationId: s.app.id }], jwks: backend.jwks, forwardBearer: false };
    const modes = [];
    for (const [mode, config, headers] of [['anonymous', { mode: 'Anonymous' }, { Origin: originA }], ['ApiKey', { mode: 'ApiKey' }, baseHeaders], ['JWT', jwt, { Origin: originA, Authorization: 'Bearer ' + backend.tokens.a }]]) {
        if (mode === 'anonymous')
            await save('rate_limit', { ...configs.rate_limit, keyBy: 'Route' });
        await save('authentication', config);
        if (mode !== 'anonymous')
            await save('rate_limit', configs.rate_limit);
        await save('ip_filter', ipConfig);
        await publish();
        const allowed = await call(0, '/network/echo/ip-' + mode, { headers });
        await save('ip_filter', { mode: 'DenyList', ranges: ['0.0.0.0/0', '::/0'] });
        await publish();
        const denied = await call(1, '/network/echo/ip-' + mode, { headers }), spoofed = await call(0, '/network/echo/ip-' + mode, { headers: { ...headers, 'X-Forwarded-For': '203.0.113.1', 'X-Real-IP': '203.0.113.1' } });
        assert.equal(allowed.status, 200);
        assert.equal(denied.status, 403);
        assert.equal(spoofed.status, 403);
        modes.push({ mode, allowedStatus: allowed.status, deniedStatus: denied.status, spoofedStatus: spoofed.status });
    }
    await record('P3-04', { modes, coverage: coverage('gateway', /IpFilterPipelineTests/) });
    await save('authentication', configs.authentication);
    await save('ip_filter', ipConfig);
    await publish();
    // Freeze before editing. Subsequent work changes must not affect this release's snapshot.
    const draft = await makeRelease(), frozen = await admin.request('/releases/' + draft.id + '/submit', { method: 'POST' }), frozenConfig = frozen.frozenCandidate.policies.find(p => p.type === 'transform').config;
    await save('transform', transformConfig.replace('"name":"X-Network-QA","value":"qa"}],"path"', '"name":"X-Network-QA","value":"new"}],"path"'));
    const workingConfig = policies.get('transform').config;
    assert.notEqual(workingConfig, frozenConfig);
    for (const reviewer of s.reviewers)
        await reviewer.request('/releases/' + frozen.id + '/approve', { method: 'POST', body: { comment: '核对提交时已冻结的修订，工作修订已变更' } });
    const nodes = await admin.request('/environments/' + scope.environmentId + '/gateway-nodes');
    const nodeB = nodes.items.find(n => n.nodeName === 'local-gateway-b');
    await ctx.compose('stop', 'gateway-b');
    try {
        const resources = await inspectResources(state), cp = resources.find(r => r.Kind === 'container' && r.Config?.Labels?.['com.docker.compose.service'] === 'control-plane'), bSecret = (await docker(['exec', cp.Id, 'cat', '/run/secrets/node-b'])).stdout.trim();
        const raw = JSON.stringify({ instanceId: nodeB.instanceId, configVersion: nodeB.currentConfigVersion, deploymentSequence: nodeB.currentDeploymentSequence, status: 'Ready', supportedSnapshotSchemas: ['2.0', '2.1', '2.2'] });
        const command = ['curl', '-sS', '-o', '/tmp/network-heartbeat-result', '-w', '%{http_code}', '-X', 'POST', '-H', 'Content-Type: application/json', '-H', 'Authorization: Bearer ' + bSecret, '--data-binary', raw, 'http://127.0.0.1:8080/internal/v1/nodes/' + nodeB.id + '/heartbeat'];
        // Runtime image may have no curl: use the fixed SDK on this owner's network, never the host/public proxy.
        const heartbeat = await docker(['run', '--rm', '--label', 'com.webapi.runtime.owner=' + state.ownerId, '--network', state.projectName + '_default', release.dependencyImages.sdk, ...command.map(x => x.replace('http://127.0.0.1:8080/', 'http://control-plane:8080/'))]);
        assert.equal(heartbeat.stdout.trim(), '204');
        const gate = await management(admin, '/releases/' + frozen.id + '/publish', { method: 'POST' });
        assert.equal(gate.status, 409);
        await writePrivate(path.join(fixtureDirectory, 'old-node-gate.json'), { status: gate.status, beforeNode: nodeB });
    }
    finally {
        await ctx.compose('start', 'gateway-b');
    }
    await until(() => admin.request('/environments/' + scope.environmentId + '/gateway-nodes'), p => p.items.find(n => n.id === nodeB.id)?.supportedSnapshotSchemas?.includes('2.3'), 90);
    await admin.request('/releases/' + frozen.id + '/publish', { method: 'POST' });
    const frozenPublished = latest = await done(frozen.id), frozenBytes = await payload(frozenPublished.targetConfigVersion), frozenSnapshot = JSON.parse(frozenBytes), currentNodes = (await admin.request('/environments/' + scope.environmentId + '/gateway-nodes')).items;
    const ackRows = JSON.parse(await sql(`SELECT coalesce(json_agg(json_build_object('nodeId',node_id,'instanceId',instance_id,'configVersion',config_version,'deploymentSequence',deployment_sequence,'snapshotHash',payload_hash,'success',success)),'[]') FROM gateway_acks WHERE release_id='${frozenPublished.id}'`));
    await record('P3-07', { release: frozenPublished, nodes: currentNodes, acks: ackRows, snapshotHash: digest(frozenBytes), frozenConfig, publishedConfig: frozenSnapshot.policies.find(p => p.type === 'transform').config, workingConfig, oldNodeStatus: 409 });
    const rollbackHistories = [];
    for (const h of histories) {
        const r = await approve(await admin.request('/releases/' + latest.id + '/rollback', { method: 'POST', body: { targetConfigVersion: h.release.targetConfigVersion } }));
        await admin.request('/releases/' + r.id + '/publish', { method: 'POST' });
        latest = await done(r.id);
        const bytes = await payload(latest.targetConfigVersion);
        assert(bytes.equals(h.bytes));
        rollbackHistories.push({ schemaVersion: h.schemaVersion, originalHash: digest(h.bytes), rollbackHash: digest(bytes), originalConfigVersion: h.release.targetConfigVersion, rollbackConfigVersion: latest.targetConfigVersion, originalDeploymentSequence: h.release.deploymentSequence, rollbackDeploymentSequence: latest.deploymentSequence, targets: latest.targets });
    }
    await save('transform', transformConfig);
    await bind(activeTypes());
    await publish();
    const inFlightPromise = call(0, '/network/echo/__gate', { method: 'POST', headers: { ...baseHeaders, 'Content-Type': 'application/json' }, body: requestSample, timeoutMs: 120000 });
    await until(() => count('/__network/echo/__gate?qa=1'), n => n === 1, 10);
    const quota = await call(0, '/network/echo/quota', { method: 'POST', headers: { ...baseHeaders, 'Content-Type': 'application/json' }, body: requestSample });
    assert.equal(quota.status, 503);
    await save('transform', transformConfig.replace('"name":"X-Network-QA","value":"qa"}],"path"', '"name":"X-Network-QA","value":"new"}],"path"'));
    await publish();
    await fetch(backendUrl + '/__fixture/network/release', { method: 'POST' });
    const inFlight = await inFlightPromise;
    assert.equal(inFlight.status, 200);
    assert.equal(inFlight.headers['x-network-qa'], 'qa');
    await record('P3-08', { histories: rollbackHistories, inFlight: { status: inFlight.status, oldMarker: 'qa', receivedMarker: inFlight.headers['x-network-qa'] }, quota: { status: quota.status } });
    await save('transform', transformConfig);
    await publish();
    const finalCall = await call(1, '/network/echo/final-' + tag);
    const logResult = await until(() => logs(finalCall.traceId), v => v.data?.items?.some(x => x.traceId === finalCall.traceId), 90), traceResult = await until(() => admin.request('/observability/traces/' + finalCall.traceId + '?' + new URLSearchParams(range())), v => v.data?.spans?.length > 0, 90), metricsResult = await until(() => admin.request('/observability/metrics?' + new URLSearchParams(range())), v => v.sourceState === 'Available' && v.data?.kpis?.some(k => k.metric === 'request_count' && k.value > 0), 120);
    const promoted = await runNetworkPromotion({ s, admin, state, release, ctx, scope, version, latest, backend, record, tag, originB, ipConfig, configs, readonly, foreign });
    const privateValues = ['PRIVATE_REQUEST_NETWORK', 'PRIVATE_RESPONSE_NETWORK', apiKey, s.password, readonlyPassword, promoted.targetKey, ...Object.values(backend.tokens)];
    const observation = { logs: logResult, trace: traceResult, metrics: metricsResult };
    assert(privateValues.every(secret => !JSON.stringify(observation).includes(secret)));
    await record('P3-10', { ...observation, privateCanariesExcluded: true });
    await writePrivate(path.join(fixtureDirectory, 'private-values.json'), privateValues);
    await writePrivate(path.join(fixtureDirectory, 'browser-context.json'), { ...common, endpoint: s.endpoint, scope, admin: { username: state.bootstrapUsername, password: s.password }, readonly: { username: readonlyUser.username, password: readonlyPassword }, routeId: route.id, versionId: version.id, apiId: s.api.id, policies: [...policies.values()].map(p => ({ id: p.id, type: p.type })), sampleJson: requestSample, requestTraceId: post.traceId, hitTraceId: hit.traceId });
    await writePrivate(path.join(fixtureDirectory, 'request-records.json'), requests);
    const result = { ...common, actual: true, complete: false, sections, pending: ['P3-11-cua', 'P3-12-protection-review'], scope, latestRelease: latest, originalInstallation: 'not-upgraded' };
    await writePrivate(path.join(fixtureDirectory, 'scenario-result.json'), result);
    return result;
}
if (process.argv[1] === import.meta.filename) {
    const [revision, imageId, cloneDirectory, fixtureDirectory] = process.argv.slice(2);
    console.log(JSON.stringify(await runNetworkPolicyScenario({ revision, imageId, cloneDirectory, fixtureDirectory }), null, 2));
}
async function runNetworkPromotion({ s, admin, state, release, ctx, scope, version, latest, backend, record, tag, originB, ipConfig, configs, readonly, foreign }) {
    const { prepareDeliveryContext } = await import('../delivery/promotion-scenario.mjs');
    const { freePorts } = await import('../runtime/acceptance.mjs');
    let source = await admin.request('/environments/' + scope.environmentId);
    source = await admin.request('/environments/' + source.id, { method: 'PUT', headers: { 'If-Match': '"' + source.revision + '"' }, body: { ...source, code: 'TEST_NETWORK_' + tag, name: '网络策略隔离TEST', isProduction: false, gatewayPublicUrl: 'http://127.0.0.1:' + state.ports.gatewayA } });
    const ports = await freePorts(), flow = await admin.request('/organizations/' + scope.organizationId + '/approval-flows', { method: 'POST', body: { name: 'Network PROD ' + tag, enabled: true, steps: [{ stepOrder: 1, roleCode: 'ApiApprover', requiredCount: 1 }, { stepOrder: 2, roleCode: 'SecurityReviewer', requiredCount: 1 }] } }), target = await admin.request('/projects/' + scope.projectId + '/environments', { method: 'POST', body: { code: 'PROD_NETWORK_' + tag, name: '网络策略隔离PROD', isProduction: true, releasePolicyId: flow.id, gatewayPublicUrl: 'http://127.0.0.1:' + ports.gatewayA } });
    await writePrivate(path.join(s.directory, 'delivery-runtime.json'), { targetEnvironmentId: target.id, ports });
    await fs.mkdir(path.join(s.directory, 'delivery-private'), { mode: 0o700 });
    for (const suffix of ['a', 'b'])
        await writePrivate(path.join(s.directory, 'delivery-private', 'node-prod-' + suffix), randomBytes(32).toString('base64url'));
    const delivery = await prepareDeliveryContext(s.directory, state, release);
    const overlayFile = path.join(s.directory, 'delivery-overlay.json'), overlay = JSON.parse(await fs.readFile(overlayFile));
    for (const name of delivery.delivery.services) {
        overlay.services[name].environment.Upstream__AllowedOrigins__2 = backend.backendOrigin;
        overlay.services[name].environment.Upstream__AllowedOrigins__3 = backend.backendOriginB;
        overlay.services[name].environment.GatewayPolicies__Transform__MaxConcurrentBodyTransforms = '1';
        overlay.services[name].environment.Observability__TraceSampleRatio = '1';
    }
    await writePrivate(overlayFile, overlay);
    await delivery.compose('run', '--rm', '--no-deps', 'init-delivery-storage');
    await delivery.compose('run', '--rm', '--no-deps', 'seed-delivery-secrets');
    await delivery.compose('up', '-d', '--force-recreate', 'control-plane', ...delivery.delivery.services);
    await until(() => admin.login(state.bootstrapUsername, s.password), () => true, 90);
    await until(() => admin.request('/environments/' + target.id + '/gateway-nodes'), p => p.items.length === 2 && p.items.every(n => !n.offline && n.supportedSnapshotSchemas?.includes('2.3')), 90);
    const cluster = await admin.request('/environments/' + target.id + '/clusters', { method: 'POST', body: { name: 'PROD真实回显 ' + tag, loadBalancingPolicy: 'RoundRobin', healthCheckEnabled: false } });
    await admin.request('/clusters/' + cluster.id + '/destinations', { method: 'POST', body: { name: 'PROD echo', address: backend.backendOrigin + '/', weight: 1, enabled: true } });
    const app = await admin.request('/applications', { method: 'POST', body: { organizationId: scope.organizationId, projectId: scope.projectId, code: 'NETWORK_PROD_' + tag, name: 'PROD独立凭证', owner: '网络策略验收' } }), key = await admin.request('/applications/' + app.id + '/credentials', { method: 'POST', body: { validFrom: new Date(Date.now() - 60000).toISOString(), expiresAt: new Date(Date.now() + 86400000).toISOString() } });
    await admin.request('/applications/' + app.id + '/permissions', { method: 'POST', body: { apiId: s.api.id, environmentId: target.id, validFrom: new Date(Date.now() - 60000).toISOString() } });
    const targetRoute = await admin.request('/environments/' + target.id + '/routes', { method: 'POST', body: { apiVersionId: version.id, routeName: 'Network PROD ' + tag, path: '/network/{**path}', methods: ['GET', 'POST', 'HEAD', 'OPTIONS'], clusterId: cluster.id, priority: 100, enabled: true, timeoutMs: 60000, requireApiKey: true } });
    const select = { baseConfigVersion: 0, versionIds: [version.id] }, baselinePreview = await admin.request('/environments/' + target.id + '/releases/preview', { method: 'POST', body: select }), baseline = await admin.request('/environments/' + target.id + '/releases', { method: 'POST', body: { ...select, resourceRevisions: baselinePreview.resourceRevisions } });
    await admin.request('/releases/' + baseline.id + '/submit', { method: 'POST' });
    for (const reviewer of s.reviewers)
        await reviewer.request('/releases/' + baseline.id + '/approve', { method: 'POST', body: { comment: 'PROD独立初始基线' } });
    await admin.request('/releases/' + baseline.id + '/publish', { method: 'POST' });
    await until(() => admin.request('/releases/' + baseline.id), r => r.state === 'Succeeded' && r.targets.length === 2 && r.targets.every(t => t.acknowledged), 120);
    const deliveryPolicy = await admin.request('/projects/' + scope.projectId + '/delivery-policy');
    await admin.request('/projects/' + scope.projectId + '/delivery-policy', { method: 'PUT', headers: { 'If-Match': '"' + deliveryPolicy.revision + '"' }, body: { sourceEnvironmentId: source.id, targetEnvironmentId: target.id, mode: 'PromotionRequired', requiredTestTypes: ['InterfaceFunction', 'Integration', 'ContractCompatibility'], verificationValidityMinutes: 1440 } });
    const actor = async (suffix, permissions, actorScope) => { const code = 'Network' + suffix + tag; let role = await admin.request('/roles', { method: 'POST', body: { code, name: code, organizationId: actorScope.organizationId } }); role = await admin.request('/roles/' + role.id + '/permissions', { method: 'PUT', headers: { 'If-Match': '"' + role.revision + '"' }, body: { permissions: ['organization.read', 'project.read', 'environment.read', 'release.read', 'route.read', 'cluster.read', 'app.read', 'policy.read', 'api.read', 'api.version.read', 'api.schema.read', ...permissions] } }); let user = await admin.request('/users', { method: 'POST', body: { username: code.toLowerCase(), displayName: code, password: s.password } }); user = await admin.request('/users/' + user.id + '/roles', { method: 'PUT', headers: { 'If-Match': '"' + user.revision + '"' }, body: { roleIds: [role.id] } }); await admin.request('/users/' + user.id + '/scopes', { method: 'PUT', headers: { 'If-Match': '"' + user.revision + '"' }, body: { scopes: [{ scope: actorScope, accessMode: 'read_write' }] } }); const client = new ContractClient(s.endpoint); await client.login(user.username, s.password); return client; };
    const acceptor = await actor('Acceptor', ['release.test.accept'], scope), verifier = await actor('Verifier', ['release.verify'], { organizationId: scope.organizationId, projectId: scope.projectId }), outsider = await actor('Foreign', [], { organizationId: foreign.id });
    const artifact = await admin.request('/releases/' + latest.id + '/artifacts', { method: 'POST' }), verificationIds = [];
    for (const type of ['InterfaceFunction', 'Integration', 'ContractCompatibility']) {
        const now = new Date().toISOString();
        const v = await admin.request('/release-artifacts/' + artifact.id + '/verifications', { method: 'POST', body: { type, result: 'Passed', startedAt: now, finishedAt: now, comment: '本轮固定镜像真实HTTP与同源完整回归核对；原日志保留在本owner验收材料' } });
        verificationIds.push(v.id);
    }
    const acceptance = await admin.request('/release-artifacts/' + artifact.id + '/test-acceptances', { method: 'POST', body: { verificationIds } });
    await acceptor.request('/test-acceptances/' + acceptance.id + '/accept', { method: 'POST', headers: { 'If-Match': '"' + acceptance.revision + '"' }, body: { comment: '独立审核同源验收材料' } });
    const targetOrigins = [originB], targetRanges = ['10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8', '::1/128'];
    const targetPolicies = new Map();
    for (const [type, config] of [['cors', { ...JSON.parse((artifact.content.routes[0].policies.find(p => p.type === 'cors')).frozenConfig), allowedOrigins: targetOrigins }], ['ip_filter', { ...ipConfig, ranges: targetRanges }]]) {
        const p = await admin.request('/projects/' + scope.projectId + '/policies', { method: 'POST', body: { name: '明确PROD ' + type + ' ' + tag, type, config: JSON.stringify(config), enabled: true } });
        targetPolicies.set(type, p);
    }
    let promotion = await admin.request('/release-promotions', { method: 'POST', body: { artifactId: artifact.id } });
    const options = await admin.request('/release-promotions/' + promotion.id + '/mapping-options'), choice = options.applications.find(a => a.id === app.id);
    assert(choice && !options.truncated);
    const mapping = { routes: artifact.content.routes.map(r => ({ artifactRouteKey: r.key, targetRouteId: targetRoute.id, clusterId: cluster.id, timeoutMs: 60000, policies: r.policies.map(p => { const targetPolicy = targetPolicies.get(p.type); return { type: p.type, priority: p.priority, ...(targetPolicy ? { targetPolicyId: targetPolicy.id, targetPolicyRevision: targetPolicy.versionNo } : {}) }; }) })), applications: [{ applicationId: app.id, credentialIds: [key.credential.id], authorizationIds: choice.authorizations.map(a => a.id), confirmSharedCredentialImpact: true }] };
    const missing = structuredClone(mapping);
    for (const r of missing.routes)
        r.policies = r.policies.map(p => p.type === 'cors' ? { type: p.type, priority: p.priority } : p);
    const missingMapping = await management(admin, '/release-promotions/' + promotion.id + '/mapping', { method: 'PUT', headers: { 'If-Match': '"' + promotion.revision + '"' }, body: missing });
    assert.equal(missingMapping.status, 422);
    const readonlyWrite = await management(readonly, '/release-promotions/' + promotion.id + '/mapping', { method: 'PUT', headers: { 'If-Match': '"' + promotion.revision + '"' }, body: mapping }), foreignRead = await management(outsider, '/release-promotions/' + promotion.id);
    assert.equal(readonlyWrite.status, 403);
    assert([403, 404].includes(foreignRead.status));
    promotion = await admin.request('/release-promotions/' + promotion.id + '/mapping', { method: 'PUT', headers: { 'If-Match': '"' + promotion.revision + '"' }, body: mapping });
    const precheck = await admin.request('/release-promotions/' + promotion.id + '/precheck', { method: 'POST', body: { confirmUpstreamHealth: true, upstreamHealthComment: '当前独立计数回显后端已真实调用' } });
    assert.equal(precheck.canSubmit, true, JSON.stringify(precheck.checks));
    promotion = await admin.request('/release-promotions/' + promotion.id);
    promotion = await admin.request('/release-promotions/' + promotion.id + '/submit', { method: 'POST', headers: { 'If-Match': '"' + promotion.revision + '"' } });
    for (const reviewer of s.reviewers)
        await reviewer.request('/releases/' + promotion.targetReleaseId + '/approve', { method: 'POST', body: { comment: '独立确认目标Origin/CIDR及完整冻结Transform' } });
    await admin.request('/releases/' + promotion.targetReleaseId + '/publish', { method: 'POST' });
    promotion = await until(() => admin.request('/release-promotions/' + promotion.id), p => p.status === 'Verifying', 120);
    const targetRelease = await admin.request('/releases/' + promotion.targetReleaseId);
    assert.equal(targetRelease.state, 'Succeeded');
    assert.equal(targetRelease.targets.length, 2);
    assert(targetRelease.targets.every(t => t.acknowledged));
    const results = [];
    for (const port of [ports.gatewayA, ports.gatewayB]) {
        const r = await networkRequest({ port, url: '/network/echo', method: 'POST', headers: { 'X-API-Key': key.apiKey, Origin: originB, 'Content-Type': 'application/json' }, body: requestSample });
        assert.equal(r.status, 200);
        assert(r.text.includes('9007199254740993'));
        assert.deepEqual(JSON.parse(r.text).body.items, ['mapped', 'response']);
        results.push(r);
    }
    const sourceKey = (await fs.readFile(path.join(s.directory, 'credential'), 'utf8')).trim();
    const deniedSource = await networkRequest({ port: ports.gatewayB, url: '/network/echo', headers: { 'X-API-Key': sourceKey, Origin: originB } });
    assert.equal(deniedSource.status, 401);
    const frozenTransform = artifact.content.routes[0].policies.find(p => p.type === 'transform').frozenConfig, targetTransform = targetRelease.frozenCandidate.policies.find(p => p.type === 'transform').config;
    assert.equal(targetTransform, frozenTransform);
    const reportText = JSON.stringify({ sourceReleaseId: latest.id, targetReleaseId: targetRelease.id, statuses: results.map(r => r.status), sourceCredentialDenied: deniedSource.status, outputs: results.map(r => r.text), transformHash: digest(frozenTransform) });
    const csrf = await verifier.request('/auth/csrf'), uploaded = await fetch(verifier.base + '/api/v1/verification-reports?promotionId=' + promotion.id, { method: 'POST', headers: { Origin: verifier.base, Cookie: [...verifier.cookies].map(([k, v]) => k + '=' + v).join('; '), 'X-CSRF-Token': csrf.token, 'Idempotency-Key': randomUUID(), 'Content-Type': 'text/plain' }, body: reportText, signal: AbortSignal.timeout(30000) });
    assert.equal(uploaded.status, 200);
    const report = await uploaded.json();
    assert.equal(report.sha256, digest(reportText));
    const verificationContext = await verifier.request('/release-promotions/' + promotion.id + '/verification-context');
    for (const type of ['EntryConnectivity', 'AuthenticationAuthorization', 'CriticalBusinessCall']) {
        const now = new Date().toISOString();
        await verifier.request('/release-promotions/' + promotion.id + '/verifications', { method: 'POST', body: { type, result: 'Passed', startedAt: now, finishedAt: now, comment: '独立PROD两个节点实际转发/凭证隔离/JSON结果已核对', reportId: report.id, expectedContextHash: verificationContext.hash } });
    }
    promotion = await until(() => admin.request('/release-promotions/' + promotion.id), p => p.status === 'Completed', 60);
    await record('P3-09', { sourceArtifact: artifact, sourceEnvironmentId: source.id, targetEnvironmentId: target.id, state: targetRelease.state, promotionStatus: promotion.status, sourceOrigins: JSON.parse(latest.frozenCandidate.policies.find(p => p.type === 'cors').config).allowedOrigins, targetOrigins, sourceRanges: ipConfig.ranges, targetRanges, frozenTransformHash: digest(frozenTransform), targetTransformHash: digest(targetTransform), missingMappingStatus: missingMapping.status, foreignStatus: foreignRead.status, readonlyStatus: readonlyWrite.status, approvalCount: targetRelease.approvalSteps.filter(a => a.status === 'Approved').length, targetRelease, targetNodes: (await admin.request('/environments/' + target.id + '/gateway-nodes')).items, actualRequests: results, sourceCredentialDenied: deniedSource.status, reportHash: report.sha256 });
    return { targetKey: key.apiKey, targetPorts: ports, targetEnvironmentId: target.id, promotionId: promotion.id };
}
