import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const repository = path.resolve(import.meta.dirname, '../..');

async function check(t, suite, outcome) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-legacy-test-'));
  t.after(() => fs.rm(root, {recursive: true, force: true}));
  const project = `tests/WebApi.${suite}.Tests/WebApi.${suite}.Tests.csproj`;
  await fs.mkdir(path.join(root, path.dirname(project)), {recursive: true});
  await fs.writeFile(path.join(root, project), '');
  await fs.mkdir(path.join(root, 'scripts'));
  await fs.copyFile(path.join(repository, 'scripts/check.sh'), path.join(root, 'scripts/check.sh'));
  await fs.cp(path.join(repository, 'scripts/contracts'), path.join(root, 'scripts/contracts'), {recursive: true});
  const args = ['--filter', 'FullyQualifiedName~One Test', '--logger', 'trx;LogFileName=caller.trx',
    '--results-directory', '/custom-results', '--', 'xUnit.MaxParallelThreads=2'];
  const calls = path.join(root, 'calls.jsonl');
  await fs.writeFile(path.join(root, 'docker'), `#!${process.execPath}
    const fs = require('node:fs');
    const args = process.argv.slice(2);
    fs.appendFileSync(process.env.CALLS, JSON.stringify(args) + '\\n');
    if (!args.includes('run')) process.exit(0);
    if (process.env.OUTCOME === 'sdk-failure') process.exit(42);
    const binding = args.find(a => a.endsWith(':/contract-completion.json'));
    if (binding && process.env.OUTCOME !== 'missing') {
      const passed = process.env.OUTCOME === 'zero' ? 0 : 1;
      const owner = process.env.OUTCOME === 'foreign' ? 'previous-run' : process.env.WEBAPI_CONTRACT_TEST_OWNER;
      fs.writeFileSync(binding.split(':')[0], JSON.stringify({owner, completed:true, executed:passed, passed, skipped:0, failed:0}));
    }
  `, {mode: 0o700});
  const result = spawnSync('bash', ['scripts/check.sh', suite.toLowerCase(), ...args], {
    cwd: root, encoding: 'utf8', timeout: 12000,
    env: {...process.env, PATH: root + path.delimiter + process.env.PATH,
      WEBAPI_NODE: process.execPath, CALLS: calls, OUTCOME: outcome},
  });
  assert.ifError(result.error);
  const commands = (await fs.readFile(calls, 'utf8')).trim().split('\n').map(line => JSON.parse(line));
  const run = commands.find(c => c.includes('run'));
  assert.deepEqual(run.slice(run.indexOf(project)), [project, '--configuration', 'Release', ...args]);
  assert.equal(commands.some(c => c.includes('down') || c.includes('-p')), false);
  const binding = run.find(a => a.endsWith(':/contract-completion.json'));
  if (binding) await assert.rejects(fs.stat(path.dirname(binding.split(':')[0])), {code: 'ENOENT'});
  return result;
}

for (const [outcome, suite] of [['zero','Domain'], ['missing','Integration'], ['foreign','Gateway']]) {
  test(`legacy ${suite} rejects a zero SDK exit with ${outcome} completion`, async t => {
    const result = await check(t, suite, outcome);
    assert.equal(result.status, 3, result.stderr);
  });
}

test('legacy backend accepts a completed run with the original test arguments', async t => {
  const result = await check(t, 'Domain', 'passed');
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /1 passed, 0 skipped, 1 total/);
});

test('legacy backend preserves the failing SDK exit code', async t => {
  const result = await check(t, 'Gateway', 'sdk-failure');
  assert.equal(result.status, 42, result.stderr);
});
