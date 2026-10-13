import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { docker } from '../../scripts/runtime/docker.mjs';
const port = async () => { const server = net.createServer(); await new Promise((r, j) => { server.once('error', j); server.listen(0, '127.0.0.1', r); }); const n = server.address().port; await new Promise(r => server.close(r)); return n; };
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
