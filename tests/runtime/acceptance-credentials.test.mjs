import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {spawnSync} from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {cleanupAcceptanceRun} from '../../scripts/acceptance-credentials.mjs';
import {LocalClient} from '../../scripts/local-client.mjs';

const rotated = ['admin', 'reader', 'approver', 'security'].map(name => `password-e2e-${name}`);

async function workspace(t) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'acceptance-credentials-'));
  t.after(() => fs.rm(directory, {recursive: true, force: true}));
  return directory;
}

test('teardown can still use the credentials before removal, including partially created runs', async t => {
  const dir = await workspace(t);
  const names = ['node-a', 'credential', ...rotated.slice(0, 2), 'password-e2e-unrelated'];
  for (const name of names) await fs.writeFile(path.join(dir, name), '');
  const receipt = await cleanupAcceptanceRun(dir, ['node-a', 'node-b'], async () => {
    assert.deepEqual((await fs.readdir(dir)).sort(), names.sort());
    return {containersRemaining: 0};
  });
  assert.deepEqual(receipt, {containersRemaining: 0, secretsRemaining: 0});
  assert.deepEqual(await fs.readdir(dir), ['password-e2e-unrelated']);
});

test('a failed resource teardown still removes files and does not invent resource counts', async t => {
  const dir = await workspace(t);
  for (const name of rotated) await fs.writeFile(path.join(dir, name), '');
  const outcome = await cleanupAcceptanceRun(dir, [], async () => { throw Error('private fixture detail'); });
  assert.equal(outcome.failed, true);
  assert.equal(outcome.secretsRemaining, 0);
  assert.equal(outcome.containersRemaining, undefined);
  assert(!JSON.stringify(outcome).includes('private fixture detail'));
  assert.deepEqual(await fs.readdir(dir), []);
});

test('an unremovable entry is counted, the other credentials are removed and a retry can finish', async t => {
  const dir = await workspace(t);
  await fs.mkdir(path.join(dir, rotated[1]));
  await fs.writeFile(path.join(dir, rotated[1], 'preserve'), '');
  for (const name of [rotated[0], rotated[2], rotated[3], 'login-hmac']) await fs.writeFile(path.join(dir, name), '');
  const outcome = await cleanupAcceptanceRun(dir, ['login-hmac'], async () => ({}));
  assert.equal(outcome.failed, true);
  assert.equal(outcome.secretsRemaining, 1);
  assert.deepEqual(await fs.readdir(dir), [rotated[1]]);
  await fs.rename(path.join(dir, rotated[1]), path.join(dir, 'retained'));
  assert.deepEqual(await cleanupAcceptanceRun(dir, ['login-hmac'], async () => ({})), {secretsRemaining: 0});
});

test('an unverifiable directory gets an unknown count and its symlink target is preserved', async t => {
  const root = await workspace(t);
  const other = path.join(root, 'other');
  await fs.mkdir(other, {mode: 0o700});
  await fs.writeFile(path.join(other, 'password'), '');
  const link = path.join(root, 'run');
  await fs.symlink(other, link);
  const receipt = await cleanupAcceptanceRun(link, ['password'], async () => ({}));
  assert.equal(receipt.failed, true);
  assert.equal(receipt.secretsRemaining, null);
  assert.deepEqual(await fs.readdir(other), ['password']);
});

for (const [name, code] of [['SIGINT', 130], ['SIGTERM', 143], ['SIGHUP', 129]]) {
  test(`a caught ${name} lets cleanup finish and preserves the signal exit status`, () => {
    const module = new URL('../../scripts/acceptance-credentials.mjs', import.meta.url);
    const program = `
      import {withAcceptanceSignals} from ${JSON.stringify(module.href)};
      const before = process.listenerCount(${JSON.stringify(name)});
      await withAcceptanceSignals(async signal => {
        const aborted = new Promise((resolve, reject) => {
          const timer = setTimeout(() => reject(Error('signal not delivered')), 1000);
          signal.addEventListener('abort', () => { clearTimeout(timer); resolve(); }, {once:true});
        });
        process.kill(process.pid, ${JSON.stringify(name)});
        try {
          await aborted;
          signal.throwIfAborted();
        } finally {
          await new Promise(resolve => setTimeout(resolve, 5));
          console.log('cleanup finished');
        }
      });
      if (process.listenerCount(${JSON.stringify(name)}) !== before) throw Error('signal handler leaked');
    `;
    const result = spawnSync(process.execPath, ['--input-type=module', '-e', program], {encoding:'utf8', timeout:5000});
    assert.equal(result.status, code, result.stderr);
    assert.match(result.stdout, /cleanup finished/);
  });
}

test('a local client cancels the CSRF lookup and its business request with the same signal', async t => {
  const acceptance = new AbortController();
  const request = new AbortController();
  const calls = [];
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    calls.push([new URL(url).pathname, options.signal]);
    return Response.json({token:'test'});
  });
  const client = new LocalClient('http://127.0.0.1', {signal: acceptance.signal});
  await client.request('/first', {method:'POST'});
  await client.request('/second', {method:'POST', signal:request.signal});
  assert.deepEqual(calls, [
    ['/api/v1/auth/csrf', acceptance.signal], ['/api/v1/first', acceptance.signal],
    ['/api/v1/auth/csrf', request.signal], ['/api/v1/second', request.signal],
  ]);
});
