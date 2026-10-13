import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { loadRelease, initializeRuntime } from '../runtime/lifecycle.mjs';
import { initializeState, loadState, writePrivate, safeFile, ownedDirectory } from '../runtime/state.mjs';
import { freePorts, cleanupFixture } from '../runtime/acceptance.mjs';
import { validateFixture } from '../runtime/fixture-guard.mjs';
import { docker, inspectResources, assertOwnership } from '../runtime/docker.mjs';
import { createGatewayPolicyFixture, cleanupGatewayFixture, assertGatewayFixtureOwner } from '../gateway-policies/fixture.mjs';
const protectedPorts = new Set([4180, 4181, 4192, 4193, 4194, 4196, 4197, 5090]);
export function assertNetworkPolicyRuntimeInput({ revision, imageId, release, directory, projectName, ports, existing }) {
    assert.match(revision ?? '', /^[a-f0-9]{40}$/);
    assert.match(imageId ?? '', /^sha256:[a-f0-9]{64}$/);
    assert.equal(release.sourceRevision, revision);
    assert.equal(release.imageId, imageId);
    validateFixture(projectName, directory);
    assert.equal(existing, false, '只允许全新独立目录，禁止接管或恢复既有运行环境');
    assert.deepEqual(Object.keys(ports).sort(), ['console', 'gatewayA', 'gatewayB']);
    assert.equal(new Set(Object.values(ports)).size, 3);
    for (const p of Object.values(ports)) {
        assert(Number.isInteger(p) && p > 0 && p <= 65535);
        assert(!protectedPorts.has(p), '禁止使用原实例或开发端口');
    }
}
export async function initializeNetworkPolicyFixture({ revision, imageId, cloneDirectory, releaseFile, bootstrapUsername = 'network-policy-admin' }) {
    await assertFixedNetworkSources(revision);
    const projectName = path.basename(path.resolve(cloneDirectory));
    let existing = false;
    try {
        await fs.lstat(cloneDirectory);
        existing = true;
    }
    catch (e) {
        if (e.code !== 'ENOENT')
            throw e;
    }
    const release = await loadRelease(releaseFile);
    let ports;
    do {
        ports = await freePorts();
    } while (Object.values(ports).some(p => protectedPorts.has(p)));
    assertNetworkPolicyRuntimeInput({ revision, imageId, release, directory: cloneDirectory, projectName, ports, existing });
    const image = JSON.parse((await docker(['image', 'inspect', imageId])).stdout)[0];
    assert.equal(image.Id, imageId);
    assert.equal(image.Config.Labels['org.opencontainers.image.revision'], revision);
    const state = await initializeState(cloneDirectory, { bootstrapUsername, ports, projectName });
    const before = await inspectResources(state);
    assert.equal(before.length, 0, '全新owner不应已有资源');
    await writePrivate(path.join(cloneDirectory, 'empty-origin.json'), { sourceRevision: revision, imageId, ownerId: state.ownerId, projectName, initializedEmpty: true, restored: false, resourcesBefore: [], createdAt: new Date().toISOString() });
    return initializeRuntime({ directory: cloneDirectory, bootstrapUsername, releaseFile });
}
export async function prepareNetworkBackend(args) {
    if (process.env.WEBAPI_RUNTIME_TEST_NETWORK_POOL !== '10.248')
        return createGatewayPolicyFixture(args);
    const { fixtureSubnet } = await import('../runtime/fixture-network.mjs');
    const state = await loadState(args.cloneDirectory), ids = (await docker(['network', 'ls', '-q'])).stdout.trim().split(/\s+/).filter(Boolean), networks = ids.length ? JSON.parse((await docker(['network', 'inspect', ...ids])).stdout) : [], occupied = networks.flatMap(n => (n.IPAM?.Config ?? []).map(c => c.Subnet));
    const defaultSubnet = fixtureSubnet(state, occupied);
    return createGatewayPolicyFixture({ ...args, defaultSubnet });
}
export async function assertNetworkOwners({ cloneDirectory, backend }) {
    const state = await loadState(cloneDirectory);
    validateFixture(state.projectName, cloneDirectory);
    assertOwnership(state, await inspectResources(state));
    if (backend) {
        await assertGatewayFixtureOwner(backend);
        assert.equal(backend.state.ownerId, state.ownerId);
    }
    return state;
}
// Explicit cleanup only; failed scenario materials and owner directories survive by default.
export async function cleanupNetworkPolicyFixture({ cloneDirectory, backend }) {
    await assertNetworkOwners({ cloneDirectory, backend });
    if (backend)
        await cleanupGatewayFixture(backend);
    try {
        await safeFile(path.join(cloneDirectory, 'delivery-runtime.json'));
    }
    catch (e) {
        if (e.code !== 'ENOENT')
            throw e;
        return cleanupFixture(cloneDirectory);
    }
    return (await import('../delivery/promotion-scenario.mjs')).cleanupPromotionContext(cloneDirectory, await loadRelease(path.join(cloneDirectory, 'release.json')));
}
export function networkFixtureDirectory(parent) { return path.join(parent, 'webapi-enterprise-local-test-' + randomUUID()); }
export async function verifyNetworkPolicySuites(options) { return (await import('../gateway-policies/verify-suites.mjs')).verifyGatewayPolicySuites({ ...options, extraTestDirectories: ['tests/network-policies'] }); }
export async function validateFixedNetworkSuites(directory, revision) {
    await ownedDirectory(directory);
    const file = path.join(directory, 'suites-result.json');
    await safeFile(file);
    const result = JSON.parse(await fs.readFile(file));
    assert.equal(result.sourceRevision, revision);
    assert.equal(result.suites.length, 5);
    assert.deepEqual(result.suites.map(s => s.name).sort(), ['console', 'domain', 'gateway', 'integration', 'runtime']);
    const { parseSuiteLog, digest } = await import('../gateway-policies/evidence.mjs');
    const logs = {};
    for (const suite of result.suites) {
        assert.equal(suite.sourceRevision, revision);
        assert.equal(suite.exitCode, 0);
        assert.equal(suite.failed, 0);
        assert.equal(suite.skipped, 0);
        assert(suite.passed > 0);
        for (const p of [suite.artifact, suite.log])
            assert(/^[a-z]+\.(?:json|log)$/.test(p));
        const summaryFile = path.join(directory, 'public', suite.artifact), logFile = path.join(directory, 'public', suite.log);
        await safeFile(summaryFile);
        await safeFile(logFile);
        const actual = JSON.parse(await fs.readFile(summaryFile));
        for (const key of ['name', 'sourceRevision', 'passed', 'failed', 'skipped', 'exitCode', 'log', 'originalLogSha256'])
            assert.deepEqual(actual[key], suite[key]);
        const text = await fs.readFile(logFile, 'utf8');
        assert.equal(digest(text), suite.originalLogSha256);
        assert(text.startsWith('WEBAPI_FIXED_SOURCE ' + revision + '\n'));
        assert(text.trimEnd().endsWith('WEBAPI_EXIT 0'));
        assert.deepEqual(parseSuiteLog(suite.name, text), { passed: suite.passed, failed: 0, skipped: 0 });
        logs[suite.name] = text;
    }
    assert(logs.runtime.includes('✔ real fixture echoes raw JSON and headers, counts, Origin Vary and gzip/br '));
    return { ...result, directory, logs };
}
export async function assertFixedNetworkSources(revision, { root = path.resolve(import.meta.dirname, '../..') } = {}) {
    assert.match(revision ?? '', /^[a-f0-9]{40}$/);
    const { execFile } = await import('node:child_process'), { promisify } = await import('node:util'), exec = promisify(execFile);
    const paths = ['src', 'console', 'scripts', 'tests', 'tools', 'deploy', 'third_party', 'Directory.Build.props', 'Directory.Packages.props'];
    const resolved = (await exec('git', ['rev-parse', revision + '^{commit}'], { cwd: root })).stdout.trim();
    assert.equal(resolved, revision);
    assert.equal((await exec('git', ['diff', revision, '--name-only', '--', ...paths], { cwd: root })).stdout, '', '工作区代码与固定镜像源不一致');
    assert.equal((await exec('git', ['ls-files', '--others', '--exclude-standard', '--', ...paths], { cwd: root })).stdout, '', '不允许未固定的额外源文件');
    for (const relative of ['scripts/network-policies/scenario.mjs', 'scripts/network-policies/evidence.mjs', 'scripts/network-policies/fixtures.mjs', 'scripts/network-policies/acceptance.mjs', 'scripts/gateway-policies/fixtures/Program.cs']) {
        const expected = (await exec('git', ['show', revision + ':' + relative], { cwd: root, encoding: 'buffer', maxBuffer: 8 * 1048576 })).stdout;
        assert((await fs.readFile(path.join(root, relative))).equals(expected));
    }
}
export async function captureNetworkPolicyProtection({ main, originalDirectory }) {
    const { execFile } = await import('node:child_process'), { promisify } = await import('node:util'), exec = promisify(execFile);
    const { snapshotDeliveryFiles, snapshotDeliveryTables } = await import('../gateway-policies/delivery.mjs');
    const state = await loadState(originalDirectory);
    assert.equal(state.projectName, 'webapi-enterprise-local');
    const release = await loadRelease(path.join(originalDirectory, 'release.json'));
    const files = await snapshotDeliveryFiles(main, originalDirectory), tables = await readProtectedTableMultisets(state);
    return { main: { head: (await exec('git', ['rev-parse', 'HEAD'], { cwd: main })).stdout.trim(), status: (await exec('git', ['status', '--short', '--untracked-files=all'], { cwd: main, maxBuffer: 16 * 1048576 })).stdout, files }, runtime: { ownerId: state.ownerId, projectName: state.projectName, ports: state.ports, binding: state.binding, sourceRevision: release.sourceRevision, imageId: release.imageId, tables } };
}
export async function makeNetworkPolicyProtectionEvidence({ before, after, sourceRevision, imageId, ownerId, projectName, modelCoverage, pauseReceipt }) {
    assert.deepEqual(before.main, after.main);
    for (const k of ['ownerId', 'projectName', 'ports', 'binding', 'sourceRevision', 'imageId'])
        assert.deepEqual(before.runtime[k], after.runtime[k]);
    assertProtectedTableMultisets(before.runtime.tables, after.runtime.tables);
    assert.equal(pauseReceipt.pauseIsHumanRequested, true);
    assert.equal(pauseReceipt.complete, true);
    assert.equal(pauseReceipt.ownedRunningContainers, 0);
    const stable = tables => Object.fromEntries(Object.entries(before.runtime.tables).filter(([, v]) => v.mode !== 'ephemeral').map(([name, v]) => {
        const available = new Map();
        for (const hash of tables[name].rowHashes)
            available.set(hash, (available.get(hash) ?? 0) + 1);
        const rowHashes = v.rowHashes.map(hash => { assert((available.get(hash) ?? 0) > 0); available.set(hash, available.get(hash) - 1); return hash; });
        return [name, { mode: v.mode, rowHashes }];
    }));
    const runtime = tables => ({ ...Object.fromEntries(Object.entries(before.runtime).filter(([key]) => key !== 'tables')), tables: stable(tables) });
    return { sourceRevision, imageId, ownerId, projectName, id: 'P3-12', actual: true, mainBefore: before.main, mainAfter: after.main, runtimeBefore: runtime(before.runtime.tables), runtimeAfter: runtime(after.runtime.tables), package2Paused: true, pauseReceipt, modelCoverage, pendingModelChanges: false };
}
export async function readProtectedTableMultisets(state) {
    const { snapshotDeliveryTables, deliverySql } = await import('../gateway-policies/delivery.mjs'), { createHash } = await import('node:crypto');
    const metadata = await snapshotDeliveryTables(state), tables = {};
    for (const [table, info] of Object.entries(metadata)) {
        assert(/^[a-zA-Z0-9_]+$/.test(table));
        assert(info.ignoredColumns.every(c => /^[a-zA-Z0-9_]+$/.test(c)));
        if (info.mode === 'ephemeral') {
            tables[table] = { mode: info.mode, rowHashes: [] };
            continue;
        }
        const expression = 'to_jsonb(t)' + (info.ignoredColumns.length ? " - ARRAY[" + info.ignoredColumns.map(c => "'" + c + "'").join(',') + "]::text[]" : '');
        const rows = JSON.parse(await deliverySql(state, `SELECT coalesce(json_agg((${expression})::text ORDER BY (${expression})::text),'[]'::json) FROM "${table}" t`));
        tables[table] = { mode: info.mode, rowHashes: rows.map(row => createHash('sha256').update(row).digest('hex')).sort() };
    }
    return tables;
}
export function assertProtectedTableMultisets(before, after) {
    assert.deepEqual(Object.keys(after).sort(), Object.keys(before).sort());
    const counts = values => {
        assert(Array.isArray(values));
        const map = new Map();
        for (const v of values) {
            assert(/^[a-f0-9]{64}$/.test(v));
            map.set(v, (map.get(v) ?? 0) + 1);
        }
        return map;
    };
    for (const [name, b] of Object.entries(before)) {
        const a = after[name];
        assert.equal(a.mode, b.mode);
        assert(['exact', 'append-preserve', 'ephemeral'].includes(b.mode));
        if (b.mode === 'ephemeral')
            continue;
        const bc = counts(b.rowHashes), ac = counts(a.rowHashes);
        if (b.mode === 'exact')
            assert.deepEqual([...ac].sort(), [...bc].sort());
        else
            for (const [key, count] of bc)
                assert((ac.get(key) ?? 0) >= count, '原行或重复行缺失 ' + name);
    }
    return true;
}
