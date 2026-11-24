import assert from 'node:assert/strict';
import {execFile} from 'node:child_process';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {test} from 'node:test';
import {pathToFileURL} from 'node:url';
import {promisify} from 'node:util';
import {toolManifest} from '../../scripts/comparisons/evidence.mjs';

const exec = promisify(execFile);
const repository = path.resolve(import.meta.dirname, '../..');
const owner = '532c3735-fd99-44f1-a599-f1fd7825fb05';
const project = 'webapi-enterprise-e2e-comparisons-' + owner;
const credentials = ['password', 'postgres-password', 'node-a', 'node-b', 'credential',
  'password-e2e-admin', 'password-e2e-reader', 'password-e2e-approver', 'password-e2e-security'];

const writeJson = (file, value) => fs.writeFile(file, JSON.stringify(value), {mode: 0o600});
const readJson = async file => JSON.parse(await fs.readFile(file, 'utf8'));
const remaining = async directory => (await fs.readdir(directory)).filter(name => credentials.includes(name)).sort();

async function fixture(t) {
  const root = await fs.realpath(await fs.mkdtemp(path.join(os.tmpdir(), 'comparison-cleanup-')));
  t.after(() => fs.rm(root, {recursive: true, force: true}));
  const scripts = path.join(root, 'scripts');
  const output = path.join(root, 'docs/evidence/version-comparison');
  const bin = path.join(root, 'bin');
  for (const dir of [bin, output, path.join(scripts, 'settings'), path.join(scripts, 'runtime'), path.join(root, 'deploy')]) {
    await fs.mkdir(dir, {recursive: true});
  }
  await fs.cp(path.join(repository, 'scripts/comparisons'), path.join(scripts, 'comparisons'), {recursive: true});
  // Keep ownership checks, evidence admission and credential cleanup on the real implementations.
  for (const name of ['runtime/state.mjs', 'settings/evidence.mjs', 'acceptance-credentials.mjs']) {
    const source = path.join(repository, 'scripts', name);
    await fs.writeFile(path.join(scripts, name), `export * from ${JSON.stringify(pathToFileURL(source).href)};\n`);
  }
  await fs.writeFile(path.join(scripts, 'runtime/build.mjs'), `
    export async function buildRelease({revision}) {
      if (process.env.COMPARISON_FIXTURE === 'build') throw Error('fixture build failed');
      return {sourceRevision: revision, imageId: 'sha256:' + 'b'.repeat(64)};
    }`);
  await fs.writeFile(path.join(scripts, 'comparisons/scenario.mjs'), `
    export async function runComparisonScenario({image}) {
      if (process.env.COMPARISON_FIXTURE === 'scenario-and-down') throw Error('fixture scenario failed');
      return {sourceRevision: image.sourceRevision, image, ui: {verified: false}};
    }`);
  await fs.writeFile(path.join(scripts, 'prepare-e2e.mjs'), `
    import fs from 'node:fs/promises';
    import path from 'node:path';
    const dir = process.env.WEBAPI_E2E_DIRECTORY;
    for (const name of ${JSON.stringify(credentials.slice(4))}) {
      await fs.writeFile(path.join(dir, name), '', {mode: 0o600});
    }
    await fs.writeFile(path.join(dir, 'context.json'), JSON.stringify({environmentId: process.env.WEBAPI_ENVIRONMENT_ID}), {mode: 0o600});
    if (process.env.COMPARISON_FIXTURE === 'signal') {
      await fs.writeFile(path.join(dir, 'signal-ready'), '');
      const parent = process.ppid;
      while (process.ppid === parent) await new Promise(resolve => setTimeout(resolve, 25));
    }`);
  for (const name of ['wait-http.mjs', 'wait-nodes.mjs']) await fs.writeFile(path.join(scripts, name), '');
  await writeJson(path.join(root, 'deploy/images.lock.json'), {sdk: 'fixture'});
  await fs.writeFile(path.join(bin, 'docker'), `#!/bin/sh
set -eu
case "$1" in
  compose)
    case " $* " in
      *' down '*) printf 'down\\n' >> "$WEBAPI_E2E_DIRECTORY/down-attempts"; [ "$COMPARISON_FIXTURE" != 'scenario-and-down' ] && [ "$COMPARISON_FIXTURE" != 'down' ];;
      *' port '*) printf '127.0.0.1:1\\n';;
      *' up -d --wait postgres redis '*) [ "$COMPARISON_FIXTURE" != 'first-up' ];;
      *' up '*|*' run '*|*' create '*) exit 0;;
      *) exit 91;;
    esac;;
  ps) exit 0;;
  network)
    if [ "$2" = ls ] && [ "$COMPARISON_FIXTURE" = 'foreign-network' ]; then printf 'other-network\\n'; fi
    if [ "$2" = ls ] && [ "$COMPARISON_FIXTURE" = 'residual-network' ] && [ -e "$WEBAPI_E2E_DIRECTORY/down-attempts" ]; then printf 'remaining-network\\n'; fi
    if [ "$2" = inspect ]; then printf '{"com.docker.compose.project":"%s","com.webapi.comparisons.owner":"foreign"}\\n' "$WEBAPI_E2E_PROJECT"; fi;;
  volume)
    if [ "$2" = inspect ]; then
      printf '{"com.docker.compose.project":"%s","com.webapi.comparisons.owner":"%s"}\\n' "$WEBAPI_E2E_PROJECT" "\${WEBAPI_E2E_PROJECT#webapi-enterprise-e2e-comparisons-}"
    fi;;
  run) exit 0;;
  *) exit 92;;
esac\n`, {mode: 0o700});
  const start = (args, mode = '', nodeArgs = []) => exec(process.execPath,
    [...nodeArgs, path.join(scripts, 'comparisons/acceptance.mjs'), ...args],
    {cwd: root, env: {...process.env, PATH: bin + path.delimiter + process.env.PATH, COMPARISON_FIXTURE: mode}});
  const cli = async (...args) => {
    try {
      const {stdout, stderr} = await start(...args);
      return {code: 0, stdout, stderr};
    } catch (error) {
      return {code: error.code, stdout: error.stdout, stderr: error.stderr};
    }
  };
  return {root, output, cli, start};
}

async function freezeSource(root) {
  for (const args of [['init', '--quiet'], ['add', 'scripts', 'deploy'],
    ['-c', 'user.name=Fixture', '-c', 'user.email=test@invalid', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'fixture']]) {
    await exec('git', args, {cwd: root});
  }
}

async function reviewFixture(t) {
  const f = await fixture(t);
  const directory = path.join(f.root, '.runtime', project);
  await fs.mkdir(directory, {recursive: true, mode: 0o700});
  const context = {owner, project, directory, tools: await toolManifest(f.root), environment: {
    WEBAPI_E2E_PROJECT: project, WEBAPI_E2E_DIRECTORY: directory,
    WEBAPI_E2E_RELATIVE_DIR: '.runtime/' + project, WEBAPI_ENVIRONMENT_ID: owner,
  }};
  await writeJson(path.join(directory, 'owner.json'), {project, owner});
  await writeJson(path.join(directory, 'review-context.json'), context);
  await writeJson(path.join(directory, 'result.json'), {ui: {verified: false}});
  await writeJson(path.join(f.output, 'verification.json'), {previous: true});
  for (const name of [...credentials, 'password-e2e-unrelated']) await fs.writeFile(path.join(directory, name), '', {mode: 0o600});
  return {...f, directory, context, close: mode => f.cli(['--cleanup', directory], mode)};
}

test('normal comparison run measures all nine credentials after a single teardown', async t => {
  const {root, output, cli} = await fixture(t);
  await freezeSource(root);
  const result = await cli(['--revision', 'HEAD']);
  assert.equal(result.code, 0, result.stderr);
  const [name] = await fs.readdir(path.join(root, '.runtime'));
  const directory = path.join(root, '.runtime', name);
  assert.deepEqual(await remaining(directory), []);
  assert.equal((await readJson(path.join(output, 'verification.json'))).cleanup.secretsRemaining, 0);
  assert.equal(await fs.readFile(path.join(directory, 'down-attempts'), 'utf8'), 'down\n');
});

for (const [mode, expected] of [['first-write', /fixture credential write failed/], ['build', /fixture build failed/], ['first-up', /Isolated comparison command failed/],
  ['scenario-and-down', /fixture scenario failed/], ['down', /cleanup/i]]) {
  test(`comparison run preserves the ${mode} failure and measures its remaining credentials`, async t => {
    const {root, output, cli} = await fixture(t);
    await freezeSource(root);
    const nodeArgs = [];
    if (mode === 'first-write') {
      const hook = path.join(root, 'write-fault.mjs');
      await fs.writeFile(hook, `
        import fs from 'node:fs/promises';
        import path from 'node:path';
        const write = fs.writeFile.bind(fs);
        fs.writeFile = async (file, ...args) => {
          if (path.basename(file) === 'password') {
            await write(file, 'partial', ...args.slice(1));
            throw Error('fixture credential write failed');
          }
          return write(file, ...args);
        };`);
      nodeArgs.push('--import', hook);
    }
    const result = await cli(['--revision', 'HEAD'], mode, nodeArgs);
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, expected);
    const [name] = await fs.readdir(path.join(root, '.runtime'));
    const directory = path.join(root, '.runtime', name);
    assert.deepEqual(await remaining(directory), []);
    const failure = await readJson(path.join(output, 'last-failure.json'));
    assert.equal(failure.complete, false);
    assert.equal(failure.cleanup.secretsRemaining, 0);
    if (['scenario-and-down', 'down'].includes(mode)) {
      assert.equal(failure.cleanup.failed, true);
      assert.notEqual(failure.cleanup.containersRemaining, 0);
      assert.equal(await fs.readFile(path.join(directory, 'down-attempts'), 'utf8'), 'down\n');
      await fs.access(path.join(directory, 'review-context.json'));
    } else if (mode === 'first-up') {
      assert.equal(failure.cleanup.networksRemaining, 0);
      assert.equal(await fs.readFile(path.join(directory, 'down-attempts'), 'utf8'), 'down\n');
      assert.equal((await readJson(path.join(directory, 'review-context.json'))).composeAttempted, true);
    } else {
      await assert.rejects(fs.access(path.join(directory, 'down-attempts')), {code: 'ENOENT'});
    }
  });
}

test('review teardown reports its actual residue, supports retry and preserves unrelated names', async t => {
  const f = await reviewFixture(t);
  const blocker = path.join(f.directory, 'password-e2e-admin');
  await fs.unlink(blocker);
  await fs.mkdir(blocker);
  await fs.writeFile(path.join(blocker, 'keep'), '');
  const first = await f.close();
  assert.notEqual(first.code, 0);
  assert.deepEqual(await remaining(f.directory), ['password-e2e-admin']);
  const failure = await readJson(path.join(f.output, 'last-failure.json'));
  assert.equal(failure.complete, false);
  assert.equal(failure.cleanup.secretsRemaining, 1);
  assert.equal(failure.cleanup.failed, true);
  assert.deepEqual(await readJson(path.join(f.output, 'verification.json')), {previous: true});
  await fs.rm(blocker, {recursive: true});
  const second = await f.close();
  assert.equal(second.code, 0, second.stderr);
  assert.equal(JSON.parse(second.stdout).cleanup.secretsRemaining, 0);
  assert.deepEqual(await remaining(f.directory), []);
  await fs.access(path.join(f.directory, 'password-e2e-unrelated'));
  await assert.rejects(fs.access(path.join(f.directory, 'review-context.json')), {code: 'ENOENT'});
});

for (const mode of ['tool-manifest', 'ui-proof']) {
  test(`invalid comparison ${mode} still revokes owned credentials and preserves the evidence error`, async t => {
    const f = await reviewFixture(t);
    if (mode === 'tool-manifest') await writeJson(path.join(f.directory, 'review-context.json'), {...f.context, tools: {}});
    if (mode === 'ui-proof') await writeJson(path.join(f.directory, 'ui-proof.json'), {screenshots: [{name: 'comparison'}]});
    const result = await f.close('down');
    assert.notEqual(result.code, 0);
    assert.match(result.stderr, mode === 'tool-manifest' ? /Acceptance tool source changed/ : /ENOENT.*comparison\.png/);
    assert.deepEqual(await remaining(f.directory), []);
    const failure = await readJson(path.join(f.output, 'last-failure.json'));
    assert.equal(failure.complete, false);
    assert.equal(failure.cleanup.secretsRemaining, 0);
    assert.equal(failure.cleanup.failed, true);
    assert.deepEqual(await readJson(path.join(f.output, 'verification.json')), {previous: true});
    await fs.access(path.join(f.directory, 'review-context.json'));
    await fs.access(path.join(f.directory, 'password-e2e-unrelated'));
  });
}

test('a foreign comparison owner marker protects its credentials and Docker resources', async t => {
  const f = await reviewFixture(t);
  await writeJson(path.join(f.directory, 'owner.json'), {project, owner: 'foreign'});
  assert.notEqual((await f.close()).code, 0);
  assert.deepEqual(await remaining(f.directory), [...credentials].sort());
  await assert.rejects(fs.access(path.join(f.directory, 'down-attempts')), {code: 'ENOENT'});
});

for (const mode of ['unparseable', 'missing-environment']) {
  test(`a verified local owner can revoke credentials with ${mode} review context`, async t => {
    const f = await reviewFixture(t);
    const file = path.join(f.directory, 'review-context.json');
    const damaged = mode === 'unparseable' ? '{' : JSON.stringify({...f.context, environment: undefined});
    await fs.writeFile(file, damaged);
    const result = await f.close();
    assert.notEqual(result.code, 0);
    assert.deepEqual(await remaining(f.directory), []);
    const failure = await readJson(path.join(f.output, 'last-failure.json'));
    assert.equal(failure.cleanup.failed, true);
    assert.equal(failure.cleanup.secretsRemaining, 0);
    assert.notEqual(failure.cleanup.containersRemaining, 0);
    assert.equal(await fs.readFile(file, 'utf8'), damaged);
    await assert.rejects(fs.access(path.join(f.directory, 'down-attempts')), {code: 'ENOENT'});
  });
}

test('conflicting valid review ownership claims protect the local directory', async t => {
  const f = await reviewFixture(t);
  await writeJson(path.join(f.directory, 'review-context.json'), {...f.context, directory: path.join(f.root, '.runtime/elsewhere')});
  assert.notEqual((await f.close()).code, 0);
  assert.deepEqual(await remaining(f.directory), [...credentials].sort());
  await assert.rejects(fs.access(path.join(f.directory, 'down-attempts')), {code: 'ENOENT'});
});

for (const mode of ['foreign-network', 'residual-network']) {
  test(`comparison ${mode} blocks a successful cleanup receipt while revoking owned files`, async t => {
    const f = await reviewFixture(t);
    const result = await f.close(mode);
    assert.notEqual(result.code, 0);
    assert.deepEqual(await remaining(f.directory), []);
    const failure = await readJson(path.join(f.output, 'last-failure.json'));
    assert.equal(failure.cleanup.secretsRemaining, 0);
    assert.equal(failure.cleanup.failed, true);
    if (mode === 'foreign-network') {
      await assert.rejects(fs.access(path.join(f.directory, 'down-attempts')), {code: 'ENOENT'});
      assert.notEqual(failure.cleanup.networksRemaining, 0);
    } else {
      assert.equal(failure.cleanup.networksRemaining, 1);
    }
    await fs.access(path.join(f.directory, 'review-context.json'));
  });
}

test('a review publication error preserves its measured cleanup and retry context', async t => {
  const f = await reviewFixture(t);
  const verification = path.join(f.output, 'verification.json');
  await fs.unlink(verification);
  await fs.mkdir(verification);
  const result = await f.close();
  assert.notEqual(result.code, 0);
  assert.match(result.stderr, /EISDIR/);
  assert.deepEqual(await remaining(f.directory), []);
  assert.equal((await readJson(path.join(f.output, 'last-failure.json'))).cleanup.secretsRemaining, 0);
  await fs.access(path.join(f.directory, 'review-context.json'));
  assert.equal(await fs.readFile(path.join(f.directory, 'down-attempts'), 'utf8'), 'down\n');
});

test('an unreadable comparison residual count cannot produce a successful receipt', async t => {
  const f = await reviewFixture(t);
  const hook = path.join(f.root, 'count-fault.mjs');
  await fs.writeFile(hook, `
    import fs from 'node:fs/promises';
    const readdir = fs.readdir.bind(fs);
    fs.readdir = async (directory, ...args) => {
      if (directory === ${JSON.stringify(f.directory)}) throw Error('private count failure');
      return readdir(directory, ...args);
    };`);
  const result = await f.cli(['--cleanup', f.directory], '', ['--import', hook]);
  assert.notEqual(result.code, 0);
  const failure = await readJson(path.join(f.output, 'last-failure.json'));
  assert.equal(failure.cleanup.failed, true);
  assert.equal(failure.cleanup.secretsRemaining, null);
  assert.deepEqual(await remaining(f.directory), []);
  assert.deepEqual(await readJson(path.join(f.output, 'verification.json')), {previous: true});
  await fs.access(path.join(f.directory, 'review-context.json'));
});

test('closing a retained comparison revokes its credentials without touching a second project', async t => {
  const f = await fixture(t);
  await freezeSource(f.root);
  const retained = await f.cli(['--revision', 'HEAD', '--review']);
  assert.equal(retained.code, 0, retained.stderr);
  const result = JSON.parse(retained.stdout.trim().split('\n').at(-1));
  const directory = path.dirname(result.reviewContextPath);
  assert.deepEqual(await remaining(directory), [...credentials].sort());
  assert.equal((await readJson(result.resultPath)).complete, false);
  const other = path.join(f.root, '.runtime', project);
  await fs.mkdir(other, {mode: 0o700});
  await fs.writeFile(path.join(other, 'password-e2e-admin'), '');
  const closed = await f.cli(['--cleanup', directory]);
  assert.equal(closed.code, 0, closed.stderr);
  assert.equal(JSON.parse(closed.stdout).cleanup.secretsRemaining, 0);
  assert.deepEqual(await remaining(directory), []);
  assert.deepEqual(await remaining(other), ['password-e2e-admin']);
});

test('SIGTERM during fixture preparation revokes the comparison credentials before exit 143', async t => {
  const f = await fixture(t);
  await freezeSource(f.root);
  const running = f.start(['--revision', 'HEAD', '--review'], 'signal');
  const finished = running.then(result => ({...result, code: 0}), error => error);
  t.after(() => { if (running.child.exitCode === null && running.child.signalCode === null) running.child.kill('SIGKILL'); });
  let directory;
  const until = Date.now() + 10000;
  while (Date.now() < until) {
    const runtime = path.join(f.root, '.runtime');
    const [name] = await fs.readdir(runtime).catch(() => []);
    if (name && await fs.stat(path.join(runtime, name, 'signal-ready')).catch(() => null)) {
      directory = path.join(runtime, name);
      break;
    }
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  assert.ok(directory, 'comparison did not reach private fixture preparation');
  assert.deepEqual(await remaining(directory), [...credentials].sort());
  assert.equal(running.child.kill('SIGTERM'), true);
  const result = await finished;
  assert.deepEqual(await remaining(directory), []);
  assert.equal(result.code, 143);
  assert.equal((await readJson(path.join(f.output, 'last-failure.json'))).cleanup.secretsRemaining, 0);
  assert.equal(await fs.readFile(path.join(directory, 'down-attempts'), 'utf8'), 'down\n');
});
