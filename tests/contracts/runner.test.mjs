import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const source = path.resolve(import.meta.dirname, '../..');
const filter = ['--filter', 'ReaderTests'];

async function run(t, {fault = '', exit = 0, mode = 'domain', split = '0', args = filter} = {}) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'contract-runner-'));
  t.after(() => fs.rm(root, {recursive: true, force: true}));
  const scripts = path.join(root, 'scripts');
  await fs.mkdir(scripts);
  await fs.copyFile(path.join(source, 'scripts/check-contracts.sh'), path.join(scripts, 'check-contracts.sh'));
  await fs.cp(path.join(source, 'scripts/contracts'), path.join(scripts, 'contracts'), {recursive: true});
  const other = path.join(root, '.runtime/other-project/postgres-password');
  await fs.mkdir(path.dirname(other), {recursive: true});
  await fs.writeFile(other, 'unrelated fixture');

  // Fail immediately after the real password writer creates its file. Record its mode only.
  const hook = path.join(root, 'after-write.cjs');
  await fs.writeFile(hook, `
    const fs = require('node:fs'), path = require('node:path');
    const write = fs.writeFileSync;
    if (process.env.FAULT === 'residue') {
      const unlink = fs.unlinkSync;
      fs.unlinkSync = name => path.basename(name) === 'postgres-password' ? undefined : unlink(name);
    }
    fs.writeFileSync = function(file, ...rest) {
      const result = write.call(this, file, ...rest);
      if (file === 1 || typeof file === 'string' && path.basename(file) === 'postgres-password') {
        const stat = file === 1 ? fs.fstatSync(1) : fs.statSync(file);
        write(process.env.CAPTURE + '.mode', String(stat.mode & 0o777));
        if (process.env.FAULT === 'write') throw Error('fixture error after password write');
        if (process.env.FAULT.startsWith('prepare-SIG')) process.kill(process.ppid, process.env.FAULT.slice(8));
      }
      return result;
    };
  `);
  const capture = path.join(root, 'calls');
  if (fault === 'existing') {
    await fs.writeFile(path.join(root, 'mkdir'), `#!/bin/sh
      /bin/mkdir "$@" || exit $?
      case "$3" in */webapi-contracts-test-*) printf 'foreign credential fixture' > "$3/postgres-password";; esac
    `, {mode: 0o700});
  }
  if (fault === 'residue') {
    await fs.writeFile(path.join(root, 'rm'), `#!/bin/sh
      for name in "$@"; do
        case "$name" in */postgres-password) exit 0;; esac
      done
      exec /bin/rm "$@"
    `, {mode: 0o700});
  }
  await fs.writeFile(path.join(root, 'docker'), `#!${process.execPath}
    const fs = require('node:fs'), path = require('node:path');
    const a = process.argv.slice(2), env = process.env, fault = env.FAULT;
    fs.appendFileSync(env.CAPTURE, JSON.stringify(a) + '\\n');
    const dir = env.WEBAPI_CONTRACT_TEST_DIRECTORY, owner = env.WEBAPI_CONTRACT_TEST_OWNER;
    function replaceDirectory() {
      fs.renameSync(dir, dir + '.original');
      fs.mkdirSync(dir);
    }
    if (['volume', 'network'].includes(a[0])) {
      if (a.at(-1) === 'fixture-external-seed') process.exit(fault === 'seed' ? 0 : 1);
      const project = env.WEBAPI_CONTRACT_TEST_PROJECT;
      if (a[1] === 'ls') {
        const names = a[0] === 'network' ? ['default'] : ['pg-test', 'nuget-test'];
        if (a[0] === 'volume' && env.WEBAPI_CONTRACT_SPLIT_POSTGRES === '1') names.push('pg-security-test', 'pg-credential-test', 'pg-import-test');
        for (const name of names) console.log(project + '_' + name);
      } else if (a[1] === 'inspect') {
        if (fault === 'replace-inspecting-network' && a[0] === 'network') replaceDirectory();
        const foreign = fault === 'foreign-' + a[0] || fault === 'foreign-group' && a.at(-1).endsWith('_pg-import-test');
        console.log(JSON.stringify({'com.docker.compose.project': project, 'com.webapi.contracts.owner': foreign ? 'foreign' : owner}));
      } else process.exit(93);
    } else if (a[0] === 'inspect') {
      if (fault === 'inspect') process.exit(61);
      console.log(JSON.stringify({'com.docker.compose.project': env.WEBAPI_CONTRACT_TEST_PROJECT,
        'com.webapi.contracts.owner': fault === 'foreign' ? 'another-owner' : owner}));
    } else if (a[0] !== 'compose') process.exit(90);
    else if (a.includes('ps')) {
      if (fault === 'ps') process.exit(62);
      console.log('fixture-container');
    } else if (a.includes('down')) {
      if (fault === 'replace-during-down') replaceDirectory();
      if (fault === 'down') process.exit(63);
      if (fault === 'signal-down') process.kill(process.ppid, 'SIGTERM');
    } else if (a.includes('run')) {
      if (fault.startsWith('foreign')) fs.linkSync(path.join(dir, 'postgres-password'), env.CAPTURE + '.bind');
      if (fault === 'signal-sdk') process.kill(process.ppid, 'SIGTERM');
      const mount = a.find(arg => arg.endsWith(':/contract-completion.json'));
      if (mount && fault !== 'missing') {
        const destination = mount.slice(0, -':/contract-completion.json'.length);
        if (path.dirname(destination) !== dir || (fs.statSync(destination).mode & 0o777) !== 0o600) process.exit(91);
        let report = {owner, completed: true, executed: 12, passed: 11, skipped: 1, failed: 0};
        if (fault === 'old') report.owner = '00000000-0000-4000-8000-000000000000';
        if (fault === 'interrupted') report.completed = false;
        if (fault === 'empty') Object.assign(report, {executed: 0, passed: 0, skipped: 0});
        if (fault === 'only-skips') Object.assign(report, {executed: 1, passed: 0, skipped: 1});
        if (fault === 'failed') Object.assign(report, {failed: 1, passed: 10});
        if (fault === 'inconsistent') report.executed = 20;
        if (fault === 'missing-field') delete report.failed;
        fs.writeFileSync(destination, fault === 'corrupt' ? '{"owner":' : JSON.stringify(report));
      }
      if (fault === 'replace-directory') {
        replaceDirectory();
        fs.writeFileSync(path.join(dir, 'postgres-password'), 'foreign credential fixture');
        fs.writeFileSync(path.join(dir, 'completion.json'), 'foreign completion fixture');
      }
      process.exit(Number(env.SDK_EXIT));
    } else if (!a.includes('up')) process.exit(92);
  `, {mode: 0o700});

  const result = spawnSync('bash', ['scripts/check-contracts.sh', mode, ...args], {cwd: root, encoding: 'utf8',
    env: {...process.env, PATH: root + path.delimiter + process.env.PATH, NODE_OPTIONS: '--require=' + hook,
      WEBAPI_NODE: process.execPath, WEBAPI_CONTRACT_SPLIT_POSTGRES: split, WEBAPI_CONTRACT_TEST_SUBNET: '',
      WEBAPI_CONTRACT_NUGET_SEED: 'fixture-external-seed', CAPTURE: capture, FAULT: fault, SDK_EXIT: String(exit)}, timeout: 15_000});
  assert.ifError(result.error);
  await fs.access(other);
  const lines = await fs.readFile(capture, 'utf8').catch(() => '');
  const calls = lines.trim() ? lines.trim().split('\n').map(line => JSON.parse(line)) : [];
  const directories = (await fs.readdir(path.join(root, '.runtime'))).filter(name => name !== 'other-project');
  const remaining = (await Promise.all(directories.map(name => fs.readdir(path.join(root, '.runtime', name))))).flat();
  return {...result, calls, root, directories, remaining, passwordMode: await fs.readFile(capture + '.mode', 'utf8').catch(() => null),
    retainedBindSize: (await fs.stat(capture + '.bind').catch(() => ({}))).size};
}

test('runner accepts a completed nonempty run and cleans only its UUID project', async t => {
  const r = await run(t);
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.passwordMode, String(0o600));
  const compose = r.calls.filter(a => a[0] === 'compose');
  const projects = compose.map(a => a[a.indexOf('-p') + 1]);
  assert.equal(new Set(projects).size, 1);
  assert.match(projects[0], /^webapi-contracts-test-[0-9a-f-]{36}$/);
  assert(compose.some(a => a.includes('down') && a.includes('--volumes')));
  assert.deepEqual(r.directories, []);
});

for (const fault of ['missing', 'empty', 'old', 'interrupted', 'only-skips', 'failed', 'corrupt', 'missing-field', 'inconsistent']) {
  test(`a zero SDK exit with ${fault} VSTest results fails the contract check`, async t => {
    const r = await run(t, {fault});
    assert.notEqual(r.status, 0, r.stderr);
    assert.deepEqual(r.directories, []);
  });
}

for (const fault of ['foreign', 'ps', 'inspect', 'down']) {
  test(`a Docker ${fault} error revokes the local password and limits teardown to verified ownership`, async t => {
    const r = await run(t, {fault});
    assert.equal(r.status, 3, r.stderr);
    assert.equal(r.calls.some(a => a.includes('down')), fault === 'down');
    assert(!r.remaining.includes('postgres-password'), 'owned password remained after ' + fault);
    if (fault === 'foreign') assert.equal(r.retainedBindSize, 0);
  });
}

for (const fault of ['foreign-volume', 'foreign-network', 'foreign-group']) {
  test(`a ${fault} owner refuses Docker teardown while revoking the owned credential`, async t => {
    const r = await run(t, {fault, mode: 'integration', split: '1'});
    assert.equal(r.status, 3, r.stderr);
    assert(!r.calls.some(a => a.includes('down')));
    assert(!r.remaining.includes('postgres-password'));
    assert.equal(r.retainedBindSize, 0);
  });
}

test('an external NuGet seed is inspected only for availability', async t => {
  const r = await run(t, {fault: 'seed'});
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(r.calls.filter(a => a.at(-1) === 'fixture-external-seed'), [['volume', 'inspect', 'fixture-external-seed']]);
  assert(r.calls.some(a => a.includes('down')));
});

test('an existing password at exclusive creation is preserved', async t => {
  const r = await run(t, {fault: 'existing'});
  assert.notEqual(r.status, 0, r.stderr);
  assert(!r.calls.some(a => a.includes('up')));
  assert(r.remaining.includes('postgres-password'));
});

test('a replaced directory preserves replacement files and revokes the original password', async t => {
  const r = await run(t, {fault: 'replace-directory'});
  assert.notEqual(r.status, 0, r.stderr);
  assert(!r.calls.some(a => a.includes('down')));
  const original = r.directories.find(name => name.endsWith('.original'));
  assert(original);
  await assert.rejects(fs.access(path.join(r.root, '.runtime', original, 'postgres-password')), {code: 'ENOENT'});
  const replacement = path.join(r.root, '.runtime', original.slice(0, -'.original'.length));
  assert.equal(await fs.readFile(path.join(replacement, 'postgres-password'), 'utf8'), 'foreign credential fixture');
  assert.equal(await fs.readFile(path.join(replacement, 'completion.json'), 'utf8'), 'foreign completion fixture');
});

for (const fault of ['replace-inspecting-network', 'replace-during-down']) {
  test(`a directory replaced during teardown is retained: ${fault}`, async t => {
    const r = await run(t, {fault});
    assert.notEqual(r.status, 0, r.stderr);
    assert.equal(r.calls.some(a => a.includes('down')), fault === 'replace-during-down');
    const original = r.directories.find(name => name.endsWith('.original'));
    assert(original && r.directories.includes(original.slice(0, -'.original'.length)));
    assert(!r.remaining.includes('postgres-password'));
  });
}

for (const fault of ['', 'foreign']) {
  test(`the original failed SDK exit survives ${fault || 'successful'} cleanup`, async t => {
    const r = await run(t, {exit: 42, fault});
    assert.equal(r.status, 42, r.stderr);
    assert.equal(r.calls.some(a => a.includes('down')), !fault);
    assert(!r.remaining.includes('postgres-password'));
  });
}

test('a password still present after an apparently successful unlink fails cleanup', async t => {
  const r = await run(t, {fault: 'residue'});
  assert(r.remaining.includes('postgres-password'));
  assert.equal(r.status, 3, r.stderr);
  assert.match(r.stderr, /password remains/i);
});

for (const [fault, code] of [['write', 1], ['prepare-SIGHUP', 129], ['prepare-SIGINT', 130], ['prepare-SIGTERM', 143], ['signal-sdk', 143], ['signal-down', 143]]) {
  test(`${fault} revokes the created password and preserves the primary exit`, async t => {
    const r = await run(t, {fault});
    assert.equal(r.passwordMode, String(0o600));
    assert(!r.remaining.includes('postgres-password'), 'owned password remained after ' + fault);
    assert.equal(r.status, code, r.stderr);
    if (fault === 'write' || fault.startsWith('prepare-')) assert(!r.calls.some(a => a[0] === 'compose'));
  });
}

test('integration preserves split PostgreSQL services and caller VSTest arguments', async t => {
  const args = [...filter, '--logger', 'console;verbosity=detailed', '--logger', 'trx;LogFileName=caller.trx',
    '--results-directory', '/caller-results', '--', 'xUnit.MaxParallelThreads=2'];
  const r = await run(t, {mode: 'integration', split: '1', args});
  assert.equal(r.status, 0, r.stderr);
  const calls = r.calls.filter(a => a[0] === 'compose');
  const up = calls.find(a => a.includes('up'));
  assert.deepEqual(up.slice(up.indexOf('--wait') + 1), ['postgres', 'postgres-security', 'postgres-credential', 'postgres-import', 'redis']);
  const sdk = calls.find(a => a.includes('run'));
  assert(sdk.includes('WEBAPI_CONTRACT_SPLIT_POSTGRES=1'));
  assert.deepEqual(sdk.slice(-args.length), args);
  assert(calls.some(a => a.includes('down') && a.includes('--volumes')));
  assert(calls.every(a => a.some(arg => arg.endsWith('/compose.contracts-postgres-groups.yml'))));
});

test('database grouping rejects non-integration suites and invalid values before Docker', async t => {
  for (const options of [{mode: 'domain', split: '1'}, {mode: 'integration', split: 'unexpected'}]) {
    const r = await run(t, options);
    assert.equal(r.status, 2, r.stderr);
    assert.deepEqual(r.calls, []);
  }
});
