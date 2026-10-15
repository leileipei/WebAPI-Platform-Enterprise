import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import http from 'node:http';
import { randomUUID } from 'node:crypto';
import { docker } from '../../scripts/runtime/docker.mjs';
import * as scenario from '../../scripts/network-policies/scenario.mjs';
import { ContractClient } from '../../scripts/contracts/scenario.mjs';
const port = async () => { const server = net.createServer(); await new Promise((r, j) => { server.once('error', j); server.listen(0, '127.0.0.1', r); }); const n = server.address().port; await new Promise(r => server.close(r)); return n; };
test('promotion target configurations cover every environment slot and retain frozen Transform', () => {
    const templates = [
        { type: 'timeout', environmentFields: ['timeoutMs'], frozenConfig: '{}' },
        { type: 'rate_limit', environmentFields: ['burst', 'refillTokens', 'windowMs'], frozenConfig: '{"algorithm":"TokenBucket","keyBy":"ApplicationRoute","redisFailureMode":"Reject"}' },
        { type: 'cors', environmentFields: ['allowedOrigins'], frozenConfig: '{"allowCredentials":true}' },
        { type: 'ip_filter', environmentFields: ['ranges'], frozenConfig: '{"mode":"AllowList"}' },
        { type: 'transform', environmentFields: [], frozenConfig: '{"requestBody":{"operations":[{"op":"add","path":"/n","value":9007199254740993}]}}' },
        { type: 'authentication', environmentFields: [], frozenConfig: '{"mode":"ApiKey"}' }
    ], before = structuredClone(templates);
    const selected = scenario.networkPolicyPromotionTargetConfigurations(templates, { targetOrigins: ['https://prod.example.test'], targetRanges: ['127.0.0.0/8'], targetTimeoutMs: 45000, targetRateLimit: { burst: 500, refillTokens: 300, windowMs: 2000 } });
    assert.deepEqual(selected.map(p => p.type), ['timeout', 'rate_limit', 'cors', 'ip_filter']);
    assert.deepEqual(selected[0].config, { timeoutMs: 45000 });
    assert.deepEqual(selected[1].config, { algorithm: 'TokenBucket', keyBy: 'ApplicationRoute', redisFailureMode: 'Reject', burst: 500, refillTokens: 300, windowMs: 2000 });
    assert.deepEqual(selected[2].config, { allowedOrigins: ['https://prod.example.test'], allowCredentials: true });
    assert.deepEqual(selected[3].config, { mode: 'AllowList', ranges: ['127.0.0.0/8'] });
    assert.deepEqual(templates, before);
    assert(!selected.some(p => p.type === 'transform' || p.type === 'authentication'));
});
test('promotion fixture requires explicit timeout and rate budgets removed from artifact templates', () => {
    assert.throws(() => scenario.networkPolicyPromotionTargetConfigurations([{ type: 'timeout', environmentFields: ['timeoutMs'], frozenConfig: '{}' }], {}), /explicit target timeout/);
    assert.throws(() => scenario.networkPolicyPromotionTargetConfigurations([{ type: 'rate_limit', environmentFields: ['burst', 'refillTokens', 'windowMs'], frozenConfig: '{"algorithm":"TokenBucket","keyBy":"Route","redisFailureMode":"Reject"}' }], {}), /explicit target rate/);
});
test('promotion fixture refuses unsupported environment slots instead of silently omitting mappings', () => {
    assert.throws(() => scenario.networkPolicyPromotionTargetConfigurations([{ type: 'authentication', environmentFields: ['issuer'], frozenConfig: '{"mode":"JWT"}' }], { targetOrigins: [], targetRanges: [] }), /unsupported fixture environment policy/);
});
test('promotion actor registration reads project policies without granting approval or publish', async () => {
    const calls = [], roleId = randomUUID(), userId = randomUUID();
    let username;
    const server = http.createServer(async (request, response) => {
        let text = ''; for await (const chunk of request) text += chunk;
        const body = text ? JSON.parse(text) : undefined;
        if (request.url === '/api/v1/users') username = body.username;
        calls.push({ route: request.url, method: request.method, body });
        response.setHeader('Content-Type', 'application/json');
        const value = request.url === '/api/v1/auth/csrf' ? { token: 'fixture-csrf' }
            : request.url === '/api/v1/roles' || request.url.includes('/permissions') ? { id: roleId, revision: 1 }
            : request.url === '/api/v1/users' || request.url.includes('/roles') ? { id: userId, username, revision: 2 }
            : request.url.includes('/scopes') ? { revision: 3 } : { authenticated: true };
        response.end(JSON.stringify(value));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const endpoint = 'http://127.0.0.1:' + server.address().port;
        const scope = { organizationId: randomUUID(), projectId: randomUUID(), environmentId: randomUUID() }, original = structuredClone(scope);
        const client = await scenario.createNetworkPolicyPromotionActor({ admin: new ContractClient(endpoint), endpoint, password: 'fixture-password-only', scope, suffix: 'Acceptor', permissions: ['release.test.accept'], tag: 'fixture' });
        assert(client instanceof ContractClient);
        const grant = calls.find(c => c.route.endsWith('/scopes'));
        assert.deepEqual(grant.body.scopes, [
            { scope: { organizationId: scope.organizationId, projectId: scope.projectId }, accessMode: 'read' },
            { scope: original, accessMode: 'read_write' }
        ]);
        assert.deepEqual(scope, original);
        const permissions = calls.find(c => c.route.endsWith('/permissions')).body.permissions;
        assert(permissions.includes('policy.read') && permissions.includes('release.test.accept'));
        for (const forbidden of ['approval.act', 'api.approve', 'release.publish', 'release.create', 'release.verify', 'policy.update']) assert(!permissions.includes(forbidden));
        assert.notEqual(userId, roleId);
        const created = calls.find(c => c.route === '/api/v1/users');
        const login = calls.find(c => c.route === '/api/v1/auth/login');
        assert.equal(login.body.username, created.body.username);
    } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});

// Real loopback wire contract for the first-change branch; this is a protocol
// regression, separate from the fixed-image TEST/PROD acceptance scenario.
async function promotionPasswordProtocol(action) {
    const parent = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-promotion-password-'));
    const directory = path.join(parent, 'webapi-enterprise-local-test-' + randomUUID());
    await fs.mkdir(path.join(directory, 'secrets'), { recursive: true, mode: 0o700 });
    const roleId = randomUUID(), userId = randomUUID();
    const scope = { organizationId: randomUUID(), projectId: randomUUID(), environmentId: randomUUID() };
    const password = ' Protocol initial password Aa1! ', calls = [];
    let username, changed = false;
    const server = http.createServer(async (request, response) => {
        let text = ''; for await (const chunk of request) text += chunk;
        const body = text ? JSON.parse(text) : undefined;
        calls.push({ route: request.url, method: request.method, body, csrf: request.headers['x-csrf-token'], key: request.headers['idempotency-key'] });
        response.setHeader('Content-Type', 'application/json');
        let value;
        if (request.url === '/api/v1/auth/csrf') value = { token: 'promotion-protocol-csrf' };
        else if (request.url === '/api/v1/roles' || request.url === `/api/v1/roles/${roleId}/permissions`) value = { id: roleId, revision: 2 };
        else if (request.url === '/api/v1/users') { username = body.username; value = { id: userId, username, revision: 1, mustChangePassword: true }; }
        else if (request.url === `/api/v1/users/${userId}/roles`) value = { id: userId, username, revision: 2, mustChangePassword: true };
        else if (request.url === `/api/v1/users/${userId}/scopes`) value = body.scopes;
        else if (request.url === '/api/v1/auth/login') value = { id: userId, username, authSource: 'local', mustChangePassword: true, permissions: [], scopes: [] };
        else if (request.url === '/api/v1/auth/change-password') { changed = true; value = { id: userId, username, mustChangePassword: false, permissions: ['release.read'], scopes: [{ scope, accessMode: 'read_write' }] }; }
        else if (request.url === '/api/v1/auth/me') value = { id: userId, username, mustChangePassword: !changed };
        else { response.statusCode = 404; value = { code: 'unknown_protocol_route' }; }
        response.end(JSON.stringify(value));
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    try {
        const endpoint = 'http://127.0.0.1:' + server.address().port;
        return await action({ parent, directory, endpoint, admin: new ContractClient(endpoint), roleId, userId, scope, password, calls });
    } finally {
        server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
        await fs.rm(parent, { recursive: true, force: true });
    }
}

test('promotion actor required initial change uses guarded fixture and keeps a private current password', async () => {
    await promotionPasswordProtocol(async ({ directory, endpoint, admin, userId, scope, password, calls }) => {
        const privatePasswords = [password];
        const client = await scenario.createNetworkPolicyPromotionActor({ admin, endpoint, password, scope, suffix: 'Acceptor', permissions: ['release.test.accept'], tag: 'required', directory, privatePasswords });
        const changes = calls.filter(c => c.route === '/api/v1/auth/change-password');
        assert.equal(changes.length, 1);
        const change = changes[0];
        assert.equal(change.body.currentPassword, password);
        assert.notEqual(change.body.newPassword, password);
        assert.equal(change.body.confirmPassword, change.body.newPassword);
        assert.equal(change.csrf, 'promotion-protocol-csrf'); assert.match(change.key, /^[a-f0-9-]{36}$/);
        const file = path.join(directory, 'secrets/qa-' + userId + '-password');
        assert.equal(await fs.readFile(file, 'utf8'), change.body.newPassword);
        assert.equal((await fs.stat(file)).mode & 0o077, 0);
        assert.deepEqual(privatePasswords, [password, change.body.newPassword]);
        assert.equal((await client.request('/auth/me')).mustChangePassword, false);
        const grants = calls.find(c => c.route.endsWith('/scopes')).body.scopes;
        assert.deepEqual(grants, [{ scope: { organizationId: scope.organizationId, projectId: scope.projectId }, accessMode: 'read' }, { scope, accessMode: 'read_write' }]);
    });
});

test('promotion actor required initial change rejects a persistent runtime directory before changing password', async () => {
    await promotionPasswordProtocol(async ({ parent, endpoint, admin, userId, scope, password, calls }) => {
        const directory = path.join(parent, 'webapi-enterprise-local'), privatePasswords = [password];
        await fs.mkdir(path.join(directory, 'secrets'), { recursive: true, mode: 0o700 });
        await assert.rejects(scenario.createNetworkPolicyPromotionActor({ admin, endpoint, password, scope, suffix: 'Acceptor', permissions: ['release.test.accept'], tag: 'refused', directory, privatePasswords }), /Explicit disposable runtime project required/);
        assert.equal(calls.filter(c => c.route === '/api/v1/auth/change-password').length, 0);
        assert.deepEqual(privatePasswords, [password]);
        await assert.rejects(fs.stat(path.join(directory, 'secrets/qa-' + userId + '-password')), { code: 'ENOENT' });
    });
});
// This launches the real C# fixture, not a mocked scenario executor.
test('real fixture echoes raw JSON and headers, counts, Origin Vary and gzip/br', async () => {
    const owner = randomUUID(), name = 'webapi-network-fixture-test-' + owner, directory = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-network-fixture-')), httpPort = await port();
    const sdk = JSON.parse(await fs.readFile(new URL('../../deploy/images.lock.json', import.meta.url))).sdk;
    const source = path.join(directory, 'source'), output = path.join(directory, 'output');
    await fs.cp(new URL('../../scripts/gateway-policies/fixtures/', import.meta.url), source, { recursive: true });
    await fs.mkdir(output);
    try {
        await docker(['run', '--rm', '--label', 'com.webapi.network-fixture-test.owner=' + owner, '--mount', 'type=bind,src=' + source + ',dst=/source', '--mount', 'type=bind,src=' + output + ',dst=/output', '--workdir', '/source', sdk, 'dotnet', 'publish', 'Fixture.csproj', '--source', '/source', '-o', '/output', '--nologo']);
        await docker(['run', '-d', '--name', name, '--label', 'com.webapi.network-fixture-test.owner=' + owner, '--mount', 'type=bind,src=' + output + ',dst=/fixture,readonly', '-p', '127.0.0.1:' + httpPort + ':18080', sdk, 'dotnet', '/fixture/Fixture.dll']);
        const origin = 'http://127.0.0.1:' + httpPort;
        let ready = false;
        for (let i = 0; i < 60; i++) {
            try {
                ready = (await fetch(origin + '/health')).ok;
                if (ready)
                    break;
            }
            catch { }
            await new Promise(r => setTimeout(r, 250));
        }
        assert(ready, 'real fixture startup failed');
        const sample = '{"n":9007199254740993,"nil":null,"items":["mapped"]}';
        const response = await fetch(origin + '/__network/echo?qa=1', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Network-QA': 'qa', Origin: 'https://a.example.test' }, body: sample });
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('vary'), 'Origin');
        const text = await response.text();
        assert(text.includes(sample), 'fixture must preserve JSON numeric tokens');
        const value = JSON.parse(text);
        assert.equal(value.method, 'POST');
        assert.equal(value.path, '/__network/echo');
        assert.equal(value.query, 'qa=1');
        assert.equal(value.requestHeader, 'qa');
        assert.equal(value.origin, 'https://a.example.test');
        assert.equal(value.receivedApiKey, false);
        for (const encoding of ['gzip', 'br']) {
            const r = await fetch(origin + '/__network/echo/' + encoding);
            assert.equal(r.headers.get('content-encoding'), encoding);
            const t = await r.text();
            assert(t.includes('9007199254740993'));
            assert.equal(Number(r.headers.get('x-fixture-decoded-bytes')), Buffer.byteLength(t));
            assert.equal(Number(r.headers.get('x-fixture-encoded-bytes')), Number(r.headers.get('content-length')));
        }
        assert.equal(await (await fetch(origin + '/__fixture/network/body?' + new URLSearchParams({ path: '/__network/echo?qa=1' }))).text(), sample);
        let finished = false;
        const held = fetch(origin + '/__network/echo/__gate').then(r => { finished = true; return r; });
        let started = false;
        for (let i = 0; i < 40; i++) {
            const snapshot = await (await fetch(origin + '/__fixture/stats')).json();
            if (snapshot['/__network/echo/__gate'] === 1) {
                started = true;
                break;
            }
            await new Promise(r => setTimeout(r, 50));
        }
        assert(started);
        assert.equal(finished, false);
        assert.equal((await fetch(origin + '/__fixture/network/release', { method: 'POST' })).status, 200);
        assert.equal((await held).status, 200);
        const stats = await (await fetch(origin + '/__fixture/stats')).json();
        assert.equal(stats['/__network/echo?qa=1'], 1);
        // Existing fixture behavior is deliberately retained.
        const legacy = await (await fetch(origin + '/legacy')).json();
        assert.equal(legacy.call, 1);
        assert.equal(typeof legacy.port, 'number');
    }
    finally {
        const info = await docker(['ps', '-aq', '--filter', 'name=^/' + name + '$', '--filter', 'label=com.webapi.network-fixture-test.owner=' + owner]);
        const ids = info.stdout.trim().split(/\s+/).filter(Boolean);
        assert(ids.length <= 1);
        if (ids.length)
            await docker(['rm', '-f', ids[0]]);
        await fs.rm(directory, { recursive: true, force: true });
    }
});
for (const problem of ['source', 'image', 'protected', 'directory', 'existing-owner'])
    test('runtime guard rejects ' + problem + ' before any resource mutation', async () => {
        const m = await import('../../scripts/network-policies/fixtures.mjs');
        const revision = 'a'.repeat(40), imageId = 'sha256:' + 'b'.repeat(64), projectName = 'webapi-enterprise-local-test-' + randomUUID();
        const input = { revision, imageId, release: { sourceRevision: revision, imageId }, directory: '/tmp/' + projectName, projectName, ports: { console: 14192, gatewayA: 14196, gatewayB: 14197 }, existing: false };
        if (problem === 'source')
            input.release.sourceRevision = 'c'.repeat(40);
        if (problem === 'image')
            input.release.imageId = 'sha256:' + 'c'.repeat(64);
        if (problem === 'protected')
            input.ports.console = 4192;
        if (problem === 'directory')
            input.directory = '/tmp/foreign';
        if (problem === 'existing-owner')
            input.existing = true;
        assert.throws(() => m.assertNetworkPolicyRuntimeInput(input));
    });
test('runtime guard rejects extra port keys even when values duplicate', async () => { const m = await import('../../scripts/network-policies/fixtures.mjs'), revision = 'a'.repeat(40), imageId = 'sha256:' + 'b'.repeat(64), projectName = 'webapi-enterprise-local-test-' + randomUUID(); assert.throws(() => m.assertNetworkPolicyRuntimeInput({ revision, imageId, release: { sourceRevision: revision, imageId }, directory: '/tmp/' + projectName, projectName, ports: { console: 14192, gatewayA: 14196, gatewayB: 14197, foreign: 14192 }, existing: false })); });
test('immutable source guard rejects a nonexistent revision before any Docker action', async () => { const m = await import('../../scripts/network-policies/fixtures.mjs'); await assert.rejects(m.assertFixedNetworkSources('0'.repeat(40))); });

// HTTP contract regression only. This local server is not formal runtime evidence.
for (const desiredConfigVersion of [null, 17]) {
    test('release driver honors empty and published environment HTTP contracts: ' + desiredConfigVersion, async () => {
        const calls = [], revisions = [{ resourceType: 'Route', resourceId: randomUUID(), revision: 3 }];
        const environmentId = randomUUID(), versionIds = [randomUUID()];
        const server = http.createServer(async (req, res) => {
            const chunks = [];
            for await (const chunk of req) chunks.push(chunk);
            const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null;
            res.setHeader('Content-Type', 'application/json');
            if (req.url === '/api/v1/auth/csrf') return res.end(JSON.stringify({ token: 'unit-csrf' }));
            calls.push({ method: req.method, path: req.url, body });
            if (req.method === 'GET') return res.end(JSON.stringify({ id: environmentId, desiredConfigVersion }));
            // BaseConfigVersion is nonnullable Int64 in the actual preview/create DTO.
            if (!Number.isInteger(body?.baseConfigVersion)) {
                res.statusCode = 400;
                return res.end(JSON.stringify({ code: 'invalid_request' }));
            }
            if (req.url.endsWith('/preview')) return res.end(JSON.stringify({ resourceRevisions: revisions }));
            res.end(JSON.stringify({ id: 'unit-draft', state: 'Draft' }));
        });
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        try {
            const client = new ContractClient('http://127.0.0.1:' + server.address().port);
            const result = await scenario.createNetworkPolicyRelease(client, environmentId, versionIds);
            assert.equal(result.state, 'Draft');
            assert.deepEqual(calls.map(c => [c.method, c.path]), [
                ['GET', '/api/v1/environments/' + environmentId],
                ['POST', '/api/v1/environments/' + environmentId + '/releases/preview'],
                ['POST', '/api/v1/environments/' + environmentId + '/releases'],
            ]);
            assert.equal(calls[1].body.baseConfigVersion, desiredConfigVersion === null ? 0 : 17);
            assert.deepEqual(calls[1].body.versionIds, versionIds);
            assert.deepEqual(calls[2].body, { ...calls[1].body, resourceRevisions: revisions });
        } finally {
            await new Promise(resolve => server.close(resolve));
        }
    });
}

test('request preview driver uses the exact direction contract and preserves raw input', async () => {
    let captured;
    const server = http.createServer(async (req, res) => {
        res.setHeader('Content-Type', 'application/json');
        if (req.url === '/api/v1/auth/csrf') return res.end(JSON.stringify({ token: 'unit-csrf' }));
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        captured = JSON.parse(Buffer.concat(chunks));
        if (captured.direction !== 'Request') {
            res.statusCode = 422;
            return res.end(JSON.stringify({ code: 'invalid_request' }));
        }
        res.end(JSON.stringify({ actualForwarding: false, outputJson: 'unit-preview' }));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const client = new ContractClient('http://127.0.0.1:' + server.address().port);
        const scope = { organizationId: randomUUID(), projectId: randomUUID(), environmentId: randomUUID() };
        const sample = '{"n":9007199254740993,"nil":null}';
        const result = await scenario.previewNetworkPolicyRequest(client, scope, scenario.transformConfig, sample);
        assert.equal(result.actualForwarding, false);
        assert.deepEqual(captured.scope, { organizationId: scope.organizationId, projectId: scope.projectId });
        assert.equal(captured.sampleJson, sample);
        assert.equal(captured.config, scenario.transformConfig);
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('first observation query is immediately usable with its actual scoped time window', async () => {
    let captured;
    const server = http.createServer((req, res) => {
        captured = new URL(req.url, 'http://unit.test');
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ sourceState: 'Available', data: { items: [] } }));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const client = new ContractClient('http://127.0.0.1:' + server.address().port);
        const scope = { organizationId: randomUUID(), projectId: randomUUID(), environmentId: randomUUID() };
        const original = structuredClone(scope), traceId = '0123456789abcdef0123456789abcdef';
        const queries = scenario.createNetworkPolicyQueries(client, scope);
        const result = await queries.logs(traceId);
        assert.equal(result.sourceState, 'Available');
        assert.equal(captured.pathname, '/api/v1/observability/logs');
        for (const [key, value] of Object.entries(scope)) assert.equal(captured.searchParams.get(key), value);
        assert.equal(captured.searchParams.get('traceId'), traceId);
        const start = Date.parse(captured.searchParams.get('start')), end = Date.parse(captured.searchParams.get('end'));
        assert(end - start >= 600000 && end - start < 601000);
        assert(Math.abs(Date.now() - end) < 5000);
        assert.deepEqual(scope, original);
        assert.equal(queries.range().environmentId, scope.environmentId);
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('formal observation window waits for complete collection before measuring new traffic', async () => {
    const calls = [], scope = { organizationId: randomUUID(), projectId: randomUUID(), environmentId: randomUUID() };
    const server = http.createServer((req, res) => {
        calls.push(new URL(req.url, 'http://unit.test'));
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ sourceState: calls.length === 1 ? 'Partial' : 'NoData', coverage: { complete: calls.length > 1 }, data: { kpis: [{ metric: 'request_count', state: calls.length === 1 ? 'Partial' : 'Available', value: calls.length === 1 ? null : 0 }] } }));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const before = Date.now(), client = new ContractClient('http://127.0.0.1:' + server.address().port);
        const window = await scenario.prepareNetworkPolicyObservationWindow(client, scope);
        assert.equal(calls.length, 2);
        const start = calls[0].searchParams.get('start');
        assert(Date.parse(start) >= before);
        assert.equal(calls[1].searchParams.get('start'), start);
        assert.equal(window.range().start, start);
        for (const query of calls) {
            assert.equal(query.pathname, '/api/v1/observability/metrics');
            for (const [key, value] of Object.entries(scope)) assert.equal(query.searchParams.get(key), value);
        }
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('formal observation correlates the actual root request and never accepts Partial or zero metrics', async () => {
    const calls = [], seen = { logs: 0, trace: 0, metrics: 0 }, actualTrace = 'a'.repeat(32), wrongTrace = 'b'.repeat(32), requestId = 'actual-request-id';
    const range = { organizationId: randomUUID(), projectId: randomUUID(), environmentId: randomUUID(), start: new Date(Date.now() - 1000).toISOString(), end: new Date().toISOString() };
    const server = http.createServer((req, res) => {
        const url = new URL(req.url, 'http://unit.test'); calls.push(url);
        res.setHeader('Content-Type', 'application/json');
        if (url.pathname.endsWith('/logs')) {
            const complete = ++seen.logs > 1;
            return res.end(JSON.stringify({ sourceState: complete ? 'Available' : 'Partial', data: { items: [{ requestId: 'other-request-id', traceId: wrongTrace }, { requestId, traceId: actualTrace }] } }));
        }
        if (url.pathname.includes('/traces/')) {
            const complete = ++seen.trace > 1;
            return res.end(JSON.stringify({ sourceState: complete ? 'Available' : 'Partial', data: { spans: [{ name: 'gateway.request', parentSpanId: null }] } }));
        }
        const attempt = ++seen.metrics;
        res.end(JSON.stringify({ sourceState: attempt === 1 ? 'Partial' : 'Available', data: { kpis: [{ metric: 'request_count', value: attempt === 2 ? 0 : 1 }] } }));
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const client = new ContractClient('http://127.0.0.1:' + server.address().port), window = { range: () => ({ ...range }) };
        const result = await scenario.observeNetworkPolicyRequest(client, window, { status: 200, traceId: wrongTrace, headers: { 'x-webapi-trace-id': requestId } });
        assert.deepEqual(seen, { logs: 2, trace: 2, metrics: 3 });
        assert.equal(result.traceId, actualTrace);
        assert.equal(result.requestId, requestId);
        for (const query of calls) assert.equal(query.searchParams.get('start'), range.start);
        assert(calls.filter(q => q.pathname.endsWith('/logs')).every(q => q.searchParams.get('keyword') === requestId && !q.searchParams.has('traceId')));
        assert(calls.filter(q => q.pathname.includes('/traces/')).every(q => q.pathname.endsWith('/' + actualTrace)));
        await assert.rejects(scenario.observeNetworkPolicyRequest(client, window, { status: 200, headers: {} }));
        await assert.rejects(scenario.observeNetworkPolicyRequest(client, window, { status: 401, headers: { 'x-webapi-trace-id': requestId } }));
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('root observation probe suppresses only its external parent while normal propagation remains', async () => {
    const parents = [];
    const server = http.createServer((req, res) => {
        parents.push(req.headers.traceparent);
        res.setHeader('X-WebApi-Trace-Id', 'actual-server-request-id');
        res.end('{}');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const port = server.address().port;
        const normal = await scenario.networkRequest({ port, url: '/normal' });
        const root = await scenario.networkRequest({ port, url: '/root', headers: { traceparent: '' } });
        assert.equal(parents[0], '00-' + normal.traceId + '-0123456789abcdef-01');
        assert.equal(parents[1], '');
        assert.equal(root.headers['x-webapi-trace-id'], 'actual-server-request-id');
        assert.notEqual(root.traceId, root.headers['x-webapi-trace-id']);
    } finally { await new Promise(resolve => server.close(resolve)); }
});

test('production entry proxies real HTTPS with a pinned CA and preserves gateway method/body/headers', async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-network-entry-')), calls = [];
    const server = http.createServer(async (req, res) => {
        const chunks = []; for await (const chunk of req) chunks.push(chunk);
        calls.push({ url: req.url, method: req.method, headers: req.headers, body: Buffer.concat(chunks).toString() });
        res.setHeader('Content-Type', 'application/json'); res.setHeader('X-Network-QA', 'qa');
        res.end('{"n":9007199254740993,"nil":null}');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let entry;
    try {
        entry = await scenario.createNetworkPolicyHttpsEntry({ directory, upstreamPort: server.address().port });
        assert.equal(new URL(entry.url).protocol, 'https:');
        const result = await scenario.networkRequest({ port: Number(new URL(entry.url).port), url: '/network/echo?qa=1', method: 'POST', headers: { Origin: 'https://b.example.test', 'X-API-Key': 'unit-only-key', 'Content-Type': 'application/json' }, body: '{"n":9007199254740993,"nil":null}', ca: entry.ca });
        assert.equal(result.status, 200); assert.equal(result.tlsVerified, true);
        assert.equal(result.text, '{"n":9007199254740993,"nil":null}');
        assert.equal(result.headers['x-network-qa'], 'qa');
        assert.equal(calls.length, 1); assert.equal(calls[0].method, 'POST');
        assert.equal(calls[0].url, '/network/echo?qa=1'); assert.equal(calls[0].body, result.text);
        assert.equal(calls[0].headers.origin, 'https://b.example.test');
        assert.equal(calls[0].headers.host, 'gateway.fixture.test');
        assert.equal(calls[0].headers['x-api-key'], 'unit-only-key');
        const rejected = await scenario.networkRequest({ port: Number(new URL(entry.url).port), url: '/outside', ca: entry.ca });
        assert.equal(rejected.status, 404); assert.equal(calls.length, 1);
    } finally { if (entry) await entry.close(); await new Promise(resolve => server.close(resolve)); await fs.rm(directory, { recursive: true, force: true }); }
});

test('production entry never disables certificate verification or overwrites an existing private key', async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-network-entry-'));
    const otherDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-network-entry-'));
    const server = http.createServer((req, res) => res.end('{}'));
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let entry, otherEntry;
    try {
        entry = await scenario.createNetworkPolicyHttpsEntry({ directory, upstreamPort: server.address().port });
        otherEntry = await scenario.createNetworkPolicyHttpsEntry({ directory: otherDirectory, upstreamPort: server.address().port });
        await assert.rejects(scenario.networkRequest({ port: Number(new URL(entry.url).port), url: '/network/echo', ca: otherEntry.ca }));
        await assert.rejects(scenario.createNetworkPolicyHttpsEntry({ directory, upstreamPort: server.address().port }));
        assert.equal((await fs.stat(path.join(directory, 'network-entry.key'))).mode & 0o077, 0);
    } finally { if (entry) await entry.close(); if (otherEntry) await otherEntry.close(); await new Promise(resolve => server.close(resolve)); await fs.rm(directory, { recursive: true, force: true }); await fs.rm(otherDirectory, { recursive: true, force: true }); }
});
