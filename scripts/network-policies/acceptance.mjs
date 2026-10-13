import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { loadState, ownedDirectory, safeFile, writePrivate } from '../runtime/state.mjs';
import { loadRelease } from '../runtime/lifecycle.mjs';
import { docker, inspectResources, assertOwnership } from '../runtime/docker.mjs';
import { validateFixture } from '../runtime/fixture-guard.mjs';
import { validateNetworkPolicyEvidence, digest } from './evidence.mjs';
export async function runNetworkPolicyAcceptance({ revision, imageId, directory, cloneDirectory, privateValues = [] }) {
    await ownedDirectory(directory);
    const state = await loadState(cloneDirectory);
    validateFixture(state.projectName, cloneDirectory);
    const release = await loadRelease(path.join(cloneDirectory, 'release.json'));
    assert.equal(release.sourceRevision, revision);
    assert.equal(release.imageId, imageId);
    await safeFile(path.join(cloneDirectory, 'empty-origin.json'));
    const origin = JSON.parse(await fs.readFile(path.join(cloneDirectory, 'empty-origin.json')));
    assert.equal(origin.ownerId, state.ownerId);
    assert.equal(origin.projectName, state.projectName);
    assert.equal(origin.sourceRevision, revision);
    assert.equal(origin.imageId, imageId);
    assert.equal(origin.initializedEmpty, true);
    assert.equal(origin.restored, false);
    assert.deepEqual(origin.resourcesBefore, []);
    const resources = await inspectResources(state);
    assertOwnership(state, resources);
    const services = ['control-plane', 'worker', 'console', 'gateway-a', 'gateway-b', 'prod-gateway-a', 'prod-gateway-b'];
    const apps = resources.filter(r => r.Kind === 'container' && services.includes(r.Config?.Labels?.['com.docker.compose.service']));
    assert.equal(apps.length, 7);
    const image = JSON.parse((await docker(['image', 'inspect', imageId])).stdout)[0];
    assert.equal(image.Id, imageId);
    assert.equal(image.Config.Labels['org.opencontainers.image.revision'], revision);
    for (const app of apps) {
        assert.equal(app.Image, imageId);
        assert(app.State.Running);
    }
    await safeFile(path.join(directory, 'acceptance-input.json'));
    const proof = JSON.parse(await fs.readFile(path.join(directory, 'acceptance-input.json')));
    assert.equal(proof.ownerId, state.ownerId);
    assert.equal(proof.projectName, state.projectName);
    assert.deepEqual(proof.runtime.state, state);
    assert.equal(await fs.realpath(proof.runtime.directory), await fs.realpath(cloneDirectory));
    await safeFile(path.join(directory, 'private-values.json'));
    const values = JSON.parse(await fs.readFile(path.join(directory, 'private-values.json')));
    assert(Array.isArray(values) && values.length > 0 && values.every(v => typeof v === 'string' && v.length > 8));
    const publicDirectory = await ownedDirectory(path.join(directory, 'public')), files = [];
    for (const artifact of proof.artifacts) {
        assert(typeof artifact.path === 'string');
        const parts = artifact.path.split('/');
        assert(!path.isAbsolute(artifact.path) && parts.every(p => p && p !== '.' && p !== '..'));
        let file = publicDirectory;
        for (const part of parts) {
            file = path.join(file, part);
            assert(!(await fs.lstat(file)).isSymbolicLink());
        }
        await safeFile(file);
        files.push({ path: artifact.path, type: 'file', content: await fs.readFile(file) });
    }
    const captured = JSON.parse(String(files.find(f => f.path === proof.images)?.content));
    assert.deepEqual(captured.resources.filter(r => r.Kind === 'container' && services.includes(r.Config?.Labels?.['com.docker.compose.service'])).map(r => r.Id).sort(), apps.map(r => r.Id).sort());
    const staticHashes = {};
    for (const [name, expected] of Object.entries(release.staticFiles)) {
        assert(name.startsWith('console/dist/') && !name.includes('..'));
        const response = await fetch('http://127.0.0.1:' + state.ports.console + '/' + name.slice('console/dist/'.length), { signal: AbortSignal.timeout(10000) });
        assert.equal(response.status, 200);
        staticHashes[name] = digest(Buffer.from(await response.arrayBuffer()));
        assert.equal(staticHashes[name], expected, '实际Console资源不属于固定镜像');
    }
    assert(Object.keys(staticHashes).length > 0);
    const result = validateNetworkPolicyEvidence(proof, files, { revision, imageId, privateValues: [...privateValues, ...values] });
    await writePrivate(path.join(directory, 'acceptance-result.json'), { ...result, actualContainers: apps.map(a => a.Id), staticHashes, proofSha256: digest(await fs.readFile(path.join(directory, 'acceptance-input.json'))), validatedAt: new Date().toISOString() });
    return result;
}
if (process.argv[1] === import.meta.filename) {
    const [revision, imageId, directory, cloneDirectory] = process.argv.slice(2);
    console.log(JSON.stringify(await runNetworkPolicyAcceptance({ revision, imageId, directory, cloneDirectory }), null, 2));
}
