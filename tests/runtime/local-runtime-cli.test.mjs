import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { initializeState } from '../../scripts/runtime/state.mjs';

const wrapper = fileURLToPath(new URL('../../scripts/local-runtime.sh', import.meta.url));
const run = (...args) => spawnSync('bash', [wrapper, ...args], {
  encoding: 'utf8',
  env: { ...process.env, WEBAPI_NODE: process.execPath }
});

test('installation help lists the operational commands and configuration inputs', () => {
  for (const flag of ['--help', '-h']) {
    const result = run(flag);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    for (const command of ['build', 'init', 'up', 'start', 'stop', 'restart', 'status', 'configure', 'complete-bootstrap']) {
      assert.match(result.stdout, new RegExp('\\b' + command + '\\b'));
    }
    for (const option of ['--origins-file', '--environment', '--username', '--password-file']) {
      assert.ok(result.stdout.includes(option), option);
    }
  }
});

test('help on a command performs no operation even with runtime options', async t => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'webapi-cli-help-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  for (const command of ['init', 'configure', 'complete-bootstrap']) {
    const result = run(command, '--directory', directory, '--help');
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    assert.match(result.stdout, /--origins-file/);
  }
  assert.deepEqual(await readdir(directory), []);
});

test('bad CLI syntax returns usage without echoing arguments and valid configure still validates inputs', () => {
  for (const args of [[], ['unknown-command'], ['unknown-command', '--help'], ['--unknown-option', 'private-value'], ['init', 'status'], ['configure', '--directory']]) {
    const result = run(...args);
    assert.equal(result.status, 2, result.stderr);
    assert.match(result.stderr, /Usage: local-runtime\.sh/);
    assert.match(result.stderr, /configure/);
    assert.doesNotMatch(result.stderr, /private-value/);
    assert.equal(result.stdout, '');
  }
  const result = run('configure');
  assert.equal(result.status, 2);
  assert.match(result.stderr, /origins JSON file required/);
});

test('configure reports a missing or malformed origins file as a configuration error', async t => {
  const fixture = await mkdtemp(path.join(os.tmpdir(), 'webapi-cli-origins-'));
  t.after(() => rm(fixture, { recursive: true, force: true }));
  const directory = path.join(fixture, 'runtime');
  await initializeState(directory, { bootstrapUsername: 'fixture-admin' });
  const original = await readFile(path.join(directory, 'runtime.json'), 'utf8');
  const config = path.join(directory, 'origins.json');
  for (const contents of [undefined, '["private-origin"']) {
    if (contents !== undefined) await writeFile(config, contents);
    const result = run('configure', '--directory', directory, '--origins-file', config);
    assert.equal(result.status, 2, result.stderr);
    assert.match(result.stderr, /origins.*JSON/i);
    assert.doesNotMatch(result.stderr, /private-origin/);
    assert.equal(result.stdout, '');
    assert.equal(await readFile(path.join(directory, 'runtime.json'), 'utf8'), original);
  }
});
