import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const repository = path.resolve(import.meta.dirname, '../..');

test('migration shell validates arguments before Docker and forwards valid arguments to the Migrator', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-migrate-cli-'));
  t.after(() => fs.rm(root, {recursive:true, force:true}));
  const calls = path.join(root, 'calls.jsonl');
  await fs.writeFile(path.join(root, 'docker'), `#!${process.execPath}
    require('node:fs').appendFileSync(process.env.CALLS, JSON.stringify(process.argv.slice(2)) + '\\n');
  `, {mode:0o700});
  async function invoke(args) {
    await fs.writeFile(calls, '');
    const result = spawnSync('bash', ['scripts/migrate.sh', ...args], {
      cwd:repository, encoding:'utf8', timeout:12000,
      env:{...process.env, PATH:root + path.delimiter + process.env.PATH, CALLS:calls,
        WEBAPI_MIGRATION_ARCHIVE_SHA256:'a'.repeat(64), WEBAPI_MIGRATION_EVIDENCE_DIR:'/workspace/test-evidence'}
    });
    assert.ifError(result.error);
    const commands = (await fs.readFile(calls, 'utf8')).split('\n').filter(Boolean).map(line => JSON.parse(line));
    return {result, commands};
  }
  for (const args of [['--help'], ['-h'], ['--unknown'], ['--help', '--bootstrap'],
    ['--run-sql', '--seed-catalog'], ['--seed-catalog', '--seed-catalog'], ['--bootstrap --seed-catalog']]) {
    const {result, commands} = await invoke(args);
    const help = args.length === 1 && ['--help', '-h'].includes(args[0]);
    assert.equal(result.status, help ? 0 : 2);
    assert.match(help ? result.stdout : result.stderr, /Usage:/);
    assert.deepEqual(commands, []);
  }
  for (const args of [[], ['--bootstrap'], ['--seed-catalog'], ['--bootstrap', '--seed-catalog'],
    ['--seed-catalog', '--bootstrap'], ['--run-sql'], ['--verify-evidence-restore'],
    ['--verify-evidence-restore-digest'], ['--inspect-evidence-archive'], ['--inspect-evidence-archive-stdin']]) {
    const {result, commands} = await invoke(args);
    assert.equal(result.status, 0);
    const inspectOnly = args[0]?.startsWith('--inspect-evidence-archive');
    assert.equal(commands.filter(command => command.includes('up')).length, inspectOnly ? 0 : 1);
    const run = commands.at(-1);
    assert.ok(run.includes('--no-deps'));
    const forwarded = run.flatMap((arg, index) => arg === '--env' ? [run[index + 1]] : []);
    for (const name of ['WEBAPI_MIGRATION_ARCHIVE_SHA256', 'WEBAPI_MIGRATION_EVIDENCE_DIR',
      'WEBAPI_MIGRATION_TRANSACTION_SECONDS', 'WEBAPI_MIGRATION_SQL_FILE', 'WEBAPI_BOOTSTRAP_PASSWORD_FILE']) {
      assert.ok(forwarded.includes(name), name);
    }
    assert.ok(!run.includes('a'.repeat(64)) && !run.includes('/workspace/test-evidence'));
    assert.deepEqual(run.slice(run.indexOf('dotnet')), ['dotnet', 'run', '--project', 'src/WebApi.Migrator', '-c', 'Release', '--', ...args]);
  }
});

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
