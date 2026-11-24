import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import {execFileSync, spawn, spawnSync} from 'node:child_process';
import {once} from 'node:events';
import {runTrafficPolicyScenario} from '../../scripts/policies/scenario.mjs';

const bootstrap = ['password', 'postgres-password', 'node-a', 'node-b', 'ip-hmac', 'cursor-key', 'login-hmac'];
const rotated = ['admin', 'reader', 'approver', 'security'].map(actor => 'password-e2e-' + actor);
const snapshots = ['original.snapshot', 'rollback.snapshot'];
const credentials = [...bootstrap, 'credential', ...rotated, ...snapshots];
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8'));
const remaining = directory => fs.readdirSync(directory).filter(name => credentials.includes(name)).sort();

// Exercise the shipped shell/Node entry points with a private checkout and replace only their
// build, Docker and service dependencies. The cleanup runs against real filesystem entries.
function fixture(t) {
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'policy-cleanup-')));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const scripts = path.join(root, 'scripts');
  fs.cpSync(new URL('../../scripts/', import.meta.url), scripts, {recursive: true});
  const runtime = path.join(root, '.runtime');
  const evidence = path.join(root, 'docs/evidence/policies');
  const pointer = path.join(runtime, 'policy-review.json');
  const other = path.join(runtime, 'unrelated');
  const bin = path.join(root, 'bin');
  function write(file, text, mode = 0o600) {
    fs.mkdirSync(path.dirname(file), {recursive: true});
    fs.writeFileSync(file, text, {mode});
  }
  write(path.join(other, rotated[0]), 'other project');
  write(path.join(root, 'deploy/images.lock.json'), JSON.stringify({sdk: 'fixture-sdk'}));
  write(path.join(root, 'console/package.json'), '{}');
  write(path.join(root, 'console/node_modules/typescript/bin/tsc'),
    "if (process.env.FIXTURE_BUILD_FAILURE) process.exit(22);");
  write(path.join(root, 'console/node_modules/vite/bin/vite.js'),
    "require('node:fs').mkdirSync('dist');");
  write(path.join(scripts, 'wait-http.mjs'), '');
  write(path.join(scripts, 'wait-nodes.mjs'), '');
  write(path.join(scripts, 'prepare-e2e.mjs'), `
    import fs from 'node:fs';
    import path from 'node:path';
    const dir = process.env.WEBAPI_E2E_DIRECTORY;
    const files = ${JSON.stringify(rotated)};
    fs.writeFileSync(path.join(dir, 'password-e2e-unrelated'), 'same project');
    for (const [index, name] of files.entries()) {
      fs.writeFileSync(path.join(dir, name), 'synthetic-private-' + name, {mode: 0o600});
      if (index === 1 && process.env.FIXTURE_RUN_SIGNAL) {
        if (process.env.FIXTURE_LATE_WRITE) {
          process.once('SIGTERM', () => {
            const deadline = Date.now() + 1700;
            const poll = setInterval(() => {
              if (fs.existsSync(path.join(dir, name)) && Date.now() < deadline) return;
              clearInterval(poll);
              fs.writeFileSync(path.join(dir, 'password-e2e-approver'), 'synthetic late credential', {mode: 0o600});
              fs.writeFileSync(path.join(dir, 'late-writer-finished'), '');
              process.exit(0);
            }, 10);
          });
          process.kill(process.ppid, process.env.FIXTURE_RUN_SIGNAL);
          await new Promise(resolve => setTimeout(resolve, 5000));
          process.exit(80);
        }
        process.kill(process.ppid, process.env.FIXTURE_RUN_SIGNAL);
        await new Promise(resolve => setTimeout(resolve, 200));
        process.exit(0);
      }
      if (index === 1 && process.env.FIXTURE_PREPARE_FAILURE) process.exit(34);
    }
    fs.writeFileSync(path.join(dir, 'credential'), 'synthetic-private-key', {mode: 0o600});
    fs.writeFileSync(path.join(dir, 'context.json'), JSON.stringify({environmentId: 'fixture'}), {mode: 0o600});
  `);
  write(path.join(scripts, 'policies/scenario.mjs'), `
    import fs from 'node:fs';
    import path from 'node:path';
    export async function runTrafficPolicyScenario({directory}) {
      for (const name of ${JSON.stringify(snapshots)}) fs.writeFileSync(path.join(directory, name), 'synthetic snapshot');
      return {complete: false, checks: []};
    }
  `);
  const preload = path.join(root, 'partial-bootstrap.cjs');
  write(preload, `
    const fs = require('node:fs/promises');
    const writeFile = fs.writeFile;
    fs.writeFile = async function(file, ...args) {
      if (require('node:path').basename(file) === 'node-a') throw Error('Fixture bootstrap write failed');
      return writeFile(file, ...args);
    };
  `);
  write(path.join(bin, 'docker'), `#!${process.execPath}
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
const args = process.argv.slice(2);
const dir = process.env.WEBAPI_E2E_DIRECTORY;
const project = process.env.WEBAPI_E2E_PROJECT;
const owner = project.slice('webapi-enterprise-e2e-policies-'.length);
const labels = {'com.docker.compose.project': project, 'com.webapi.policies.owner': owner};
const down = path.join(dir, 'down-attempts');
if (args[0] === 'image' && args[1] === 'inspect') {
  const hash = createHash('sha256').update(fs.readFileSync(path.join(dir, 'build/source-manifest.json'))).digest('hex');
  console.log(JSON.stringify([{Id: 'fixture-image', Config: {Labels: {'com.webapi.policies.source-manifest': hash}}}]));
} else if (args[0] === 'inspect' || (args[0] === 'volume' && args[1] === 'inspect')) {
  if (process.env.FIXTURE_FOREIGN_OWNER) labels['com.webapi.policies.owner'] = 'other-owner';
  if (args.at(-1) === project + '_lpm' && process.env.FIXTURE_LEGACY_VOLUME) {
    delete labels['com.docker.compose.project'];
    if (process.env.FIXTURE_LEGACY_VOLUME === 'conflicting') labels['com.docker.compose.project'] = 'another-project';
    if (process.env.FIXTURE_LEGACY_VOLUME === 'named-foreign' ||
        process.env.FIXTURE_LEGACY_VOLUME === 'replaced' && fs.existsSync(down)) labels['com.webapi.policies.owner'] = 'other-owner';
  }
  console.log(JSON.stringify(labels));
} else if (args[0] === 'ps') {
  if (process.env.FIXTURE_FOREIGN_OWNER) console.log('foreign');
  if (process.env.FIXTURE_RESOURCES_REMAIN && fs.existsSync(down)) console.log('container-a\\ncontainer-b');
} else if (args[0] === 'volume' && args[1] === 'ls') {
  if (process.env.FIXTURE_RESOURCES_REMAIN && fs.existsSync(down)) console.log(project + '_pg');
  const legacy = process.env.FIXTURE_LEGACY_VOLUME;
  if (legacy && !fs.existsSync(path.join(dir, 'volume-removed')) && (args.at(-1).startsWith('name=') ||
    legacy !== 'named-foreign' && args.includes('label=com.webapi.policies.owner=' + owner))) console.log(project + '_lpm');
} else if (args[0] === 'volume' && args[1] === 'rm') {
  fs.appendFileSync(path.join(dir, 'volume-rm-attempts'), args.at(-1) + '\\n');
  if (args.at(-1) !== project + '_lpm' || process.env.FIXTURE_LEGACY_VOLUME !== 'legacy') process.exit(46);
  fs.writeFileSync(path.join(dir, 'volume-removed'), '');
} else if (args[0] === 'volume' && args[1] === 'create' && process.env.FIXTURE_CHECK_CREATED_VOLUMES) {
  const volume = args.at(-1).slice(project.length + 1);
  if (!args.includes('com.docker.compose.project=' + project) || !args.includes('com.docker.compose.volume=' + volume)
    || !args.includes('com.webapi.policies.owner=' + owner)) process.exit(45);
} else if (args[0] === 'compose' && args.includes('port')) {
  console.log('127.0.0.1:12345');
} else if (args[0] === 'compose' && args.includes('down')) {
  fs.appendFileSync(down, 'attempt\\n');
  if (process.env.FIXTURE_REVIEW_SIGNAL) process.kill(process.ppid, process.env.FIXTURE_REVIEW_SIGNAL);
  if (process.env.FIXTURE_SWITCH_POINTER) fs.writeFileSync(path.join(path.dirname(dir), 'policy-review.json'), process.env.FIXTURE_SWITCH_POINTER);
  if (process.env.FIXTURE_SECRET_REMAINS) {
    const file = path.join(dir, 'password-e2e-reader');
    fs.unlinkSync(file);
    fs.mkdirSync(file);
  }
  if (process.env.FIXTURE_DOWN_FAILURE) {console.error('synthetic-private-docker-output'); process.exit(43);}
}
`, 0o700);
  execFileSync('git', ['init', '-q', root]);
  execFileSync('git', ['-C', root, '-c', 'user.name=test', '-c', 'user.email=test@localhost',
    'commit', '-qm', 'fixture', '--allow-empty']);
  function run(mode = 'e2e', env = {}) {
    const cli = mode === 'cleanup' ? [process.execPath, ['scripts/policies/cleanup-review.mjs']]
      : ['bash', ['scripts/check-policies.sh', mode]];
    const result = spawnSync(...cli, {cwd: root, encoding: 'utf8', env: {
      ...process.env, PATH: bin + path.delimiter + process.env.PATH, WEBAPI_NODE: process.execPath, ...env,
    }});
    assert.ifError(result.error);
    assert.equal(fs.readFileSync(path.join(other, rotated[0]), 'utf8'), 'other project');
    return result;
  }
  function runs() {
    return fs.readdirSync(runtime).filter(n => n.startsWith('webapi-enterprise-e2e-policies-')).map(n => path.join(runtime, n));
  }
  return {root, run, runs, pointer, evidence, preload};
}

test('policy E2E records measured cleanup and keeps unrelated evidence', t => {
  const f = fixture(t);
  const run = f.run('e2e', {FIXTURE_CHECK_CREATED_VOLUMES: '1'});
  assert.equal(run.status, 0, run.stderr);
  const [dir] = f.runs();
  assert.deepEqual(remaining(dir), []);
  assert.equal(fs.readFileSync(path.join(dir, 'password-e2e-unrelated'), 'utf8'), 'same project');
  const result = readJson(path.join(f.evidence, 'verification.json'));
  assert.equal(result.complete, true);
  assert.deepEqual(result.cleanup, {containersRemaining: 0, volumesRemaining: 0, secretsRemaining: 0});
});

for (const stage of ['bootstrap', 'build']) {
  test(`policy ${stage} failure removes earlier credential writes before Compose`, t => {
    const f = fixture(t);
    const result = f.run('e2e', stage === 'bootstrap' ? {NODE_OPTIONS: '--require=' + f.preload} : {FIXTURE_BUILD_FAILURE: '1'});
    assert.notEqual(result.status, 0);
    const [dir] = f.runs();
    assert.deepEqual(remaining(dir), []);
    assert.equal(fs.existsSync(path.join(dir, 'down-attempts')), false);
    const failure = readJson(path.join(f.evidence, 'last-failure.json'));
    assert.match(failure.message, stage === 'bootstrap' ? /bootstrap write failed/ : /tsc.*failed/);
    assert.equal(failure.cleanup.secretsRemaining, 0);
    assert.notEqual(failure.cleanup.containersRemaining, 0);
    assert.notEqual(failure.cleanup.volumesRemaining, 0);
  });
}

test('policy partial rotation failure survives a secondary teardown failure', t => {
  const f = fixture(t);
  const result = f.run('e2e', {FIXTURE_PREPARE_FAILURE: '1', FIXTURE_DOWN_FAILURE: '1'});
  assert.notEqual(result.status, 0);
  const [dir] = f.runs();
  assert.deepEqual(remaining(dir), []);
  const failure = readJson(path.join(f.evidence, 'last-failure.json'));
  assert.equal(failure.complete, false);
  assert.match(failure.message, /prepare-e2e.*failed/);
  assert.equal(failure.cleanup.failed, true);
  assert.equal(failure.cleanup.secretsRemaining, 0);
  assert.notEqual(failure.cleanup.containersRemaining, 0);
  assert.doesNotMatch(JSON.stringify(failure), /synthetic-private/);
  assert.deepEqual(readJson(path.join(f.evidence, 'failed-attempts.json')), [failure]);
});

test('SIGTERM during policy preparation reaps the child and returns 143 after cleanup', t => {
  const f = fixture(t);
  const run = f.run('e2e', {FIXTURE_RUN_SIGNAL: 'SIGTERM'});
  const [dir] = f.runs();
  assert.deepEqual(remaining(dir), []);
  assert.equal(run.status, 143);
  const failure = readJson(path.join(f.evidence, 'last-failure.json'));
  assert.equal(failure.complete, false);
  assert.equal(failure.cleanup.secretsRemaining, 0);
  assert.equal(fs.readFileSync(path.join(dir, 'down-attempts'), 'utf8'), 'attempt\n');
});

test('policy cleanup waits for a signalled credential writer to close before counting', t => {
  const f = fixture(t);
  const run = f.run('e2e', {FIXTURE_RUN_SIGNAL: 'SIGTERM', FIXTURE_LATE_WRITE: '1'});
  const [dir] = f.runs();
  assert.equal(run.status, 143);
  assert.equal(fs.existsSync(path.join(dir, 'late-writer-finished')), true);
  assert.deepEqual(remaining(dir), []);
  assert.equal(readJson(path.join(f.evidence, 'last-failure.json')).cleanup.secretsRemaining, 0);
});

test('policy cancellation interrupts an in-flight scenario request', async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'policy-request-abort-'));
  t.after(() => fs.rmSync(directory, {recursive: true, force: true}));
  for (const name of ['password', 'credential']) fs.writeFileSync(path.join(directory, name), 'synthetic');
  const controller = new AbortController();
  const interrupted = Error('fixture cancellation');
  t.mock.method(globalThis, 'fetch', async (_url, options) => {
    controller.abort(interrupted);
    options.signal?.throwIfAborted();
    throw Error('The scenario request ignored cancellation');
  });
  await assert.rejects(runTrafficPolicyScenario({directory, context: {passwordFiles: {}},
    endpoints: {control_plane: 'http://127.0.0.1'}, signal: controller.signal}), error => error === interrupted);
});

test('SIGINT during policy review teardown allows the receipt and pointer release before exit 130', t => {
  const f = fixture(t);
  const review = f.run('browser');
  assert.equal(review.status, 0, review.stderr);
  const active = readJson(f.pointer);
  const cleanup = f.run('cleanup', {FIXTURE_REVIEW_SIGNAL: 'SIGINT'});
  assert.equal(cleanup.status, 130);
  assert.deepEqual(remaining(active.directory), []);
  assert.equal(readJson(path.join(f.evidence, 'cleanup.json')).secretsRemaining, 0);
  assert.equal(fs.existsSync(f.pointer), false);
});

for (const kind of ['secret', 'resources']) {
  test(`policy E2E rejects success and reports measured ${kind} residue`, t => {
    const f = fixture(t);
    const result = f.run('e2e', kind === 'secret' ? {FIXTURE_SECRET_REMAINS: '1'} : {FIXTURE_RESOURCES_REMAIN: '1'});
    assert.notEqual(result.status, 0);
    const [dir] = f.runs();
    const failure = readJson(path.join(f.evidence, 'last-failure.json'));
    assert.equal(failure.cleanup.failed, true);
    assert.equal(failure.cleanup.secretsRemaining, kind === 'secret' ? 1 : 0);
    assert.deepEqual(remaining(dir), kind === 'secret' ? ['password-e2e-reader'] : []);
    assert.equal(failure.cleanup.containersRemaining, kind === 'resources' ? 2 : 0);
    assert.equal(failure.cleanup.volumesRemaining, kind === 'resources' ? 1 : 0);
    assert.equal(fs.readFileSync(path.join(dir, 'down-attempts'), 'utf8'), 'attempt\n');
    assert.equal(fs.existsSync(path.join(f.evidence, 'verification.json')), false);
  });
}

for (const label of ['legacy', 'conflicting', 'stuck', 'replaced', 'named-foreign']) {
  test(`policy teardown recognizes a ${label} owner-labelled HMAC volume`, t => {
    const f = fixture(t);
    const retained = f.run('browser');
    assert.equal(retained.status, 0, retained.stderr);
    const active = readJson(f.pointer);
    const cleanup = f.run('cleanup', {FIXTURE_LEGACY_VOLUME: label});
    if (label === 'legacy') assert.equal(cleanup.status, 0, cleanup.stderr);
    else assert.notEqual(cleanup.status, 0);
    assert.deepEqual(remaining(active.directory), []);
    assert.equal(fs.existsSync(path.join(active.directory, 'down-attempts')), ['legacy', 'stuck', 'replaced'].includes(label));
    assert.equal(fs.existsSync(path.join(active.directory, 'volume-rm-attempts')), ['legacy','stuck'].includes(label));
    const receipt = readJson(path.join(f.evidence, 'cleanup.json'));
    assert.equal(Boolean(receipt.failed), label !== 'legacy');
    if (label === 'legacy') assert.equal(receipt.volumesRemaining, 0);
    if (['stuck', 'replaced'].includes(label)) assert.equal(receipt.volumesRemaining, 1);
    assert.equal(fs.existsSync(f.pointer), label !== 'legacy');
  });
}

test('a foreign owner blocks policy review teardown, cleans local secrets and permits retry', t => {
  const f = fixture(t);
  const review = f.run('browser');
  assert.equal(review.status, 0, review.stderr);
  const {directory, project} = readJson(f.pointer);
  assert.deepEqual(remaining(directory), [...credentials].sort());
  const rejected = f.run('cleanup', {FIXTURE_FOREIGN_OWNER: '1'});
  assert.notEqual(rejected.status, 0);
  assert.equal(fs.existsSync(path.join(directory, 'down-attempts')), false);
  assert.deepEqual(remaining(directory), []);
  const receiptFile = path.join(f.evidence, 'cleanup.json');
  const rejectedReceipt = readJson(receiptFile);
  assert.equal(rejectedReceipt.failed, true);
  assert.equal(rejectedReceipt.secretsRemaining, 0);
  assert.equal(readJson(f.pointer).project, project);
  const retry = f.run('cleanup');
  assert.equal(retry.status, 0, retry.stderr);
  const receipt = readJson(receiptFile);
  assert.equal(receipt.project, project);
  for (const key of ['secretsRemaining', 'volumesRemaining', 'containersRemaining']) assert.equal(receipt[key], 0);
  assert.equal(fs.existsSync(f.pointer), false);
});

test('an existing policy review is preserved when a second review requests the pointer', t => {
  const f = fixture(t);
  const first = f.run('browser');
  assert.equal(first.status, 0, first.stderr);
  const kept = readJson(f.pointer);
  const second = f.run('browser');
  assert.notEqual(second.status, 0);
  assert.deepEqual(readJson(f.pointer), kept);
  assert.deepEqual(remaining(kept.directory), [...credentials].sort());
  for (const dir of f.runs().filter(dir => dir !== kept.directory)) assert.deepEqual(remaining(dir), []);
});

test('failed policy review publication cleans its own credentials and releases its pointer', t => {
  const f = fixture(t);
  fs.mkdirSync(path.join(f.evidence, 'verification.json'), {recursive: true});
  const run = f.run('browser');
  assert.notEqual(run.status, 0);
  const [directory] = f.runs();
  const failure = readJson(path.join(f.evidence, 'last-failure.json'));
  assert.match(failure.message, /verification\.json/);
  assert.deepEqual(failure.cleanup, {containersRemaining: 0, volumesRemaining: 0, secretsRemaining: 0});
  assert.deepEqual(remaining(directory), []);
  assert.equal(fs.existsSync(f.pointer), false);
});

test('a policy review pointer pointing at a sibling directory cannot clear either project', t => {
  const f = fixture(t);
  const review = f.run('browser');
  assert.equal(review.status, 0, review.stderr);
  const active = readJson(f.pointer);
  const sibling = path.join(path.dirname(active.directory), 'unrelated');
  fs.copyFileSync(path.join(active.directory, 'review-context.json'), path.join(sibling, 'review-context.json'));
  fs.writeFileSync(f.pointer, JSON.stringify({...active, directory: sibling}));
  const rejected = f.run('cleanup');
  assert.notEqual(rejected.status, 0);
  assert.deepEqual(remaining(active.directory), [...credentials].sort());
  assert.equal(fs.existsSync(path.join(active.directory, 'down-attempts')), false);
  assert.equal(readJson(f.pointer).directory, sibling);
});

test('a review receipt write failure leaves a retryable pointer', t => {
  const f = fixture(t);
  const review = f.run('browser');
  assert.equal(review.status, 0, review.stderr);
  const active = readJson(f.pointer);
  const receipt = path.join(f.evidence, 'cleanup.json');
  fs.mkdirSync(receipt);
  const first = f.run('cleanup');
  assert.notEqual(first.status, 0);
  assert.deepEqual(readJson(f.pointer), active);
  assert.deepEqual(remaining(active.directory), []);
  fs.rmdirSync(receipt);
  const retry = f.run('cleanup');
  assert.equal(retry.status, 0, retry.stderr);
  assert.equal(readJson(receipt).secretsRemaining, 0);
  assert.equal(fs.existsSync(f.pointer), false);
});

for (const damage of ['unreadable context', 'missing Compose arguments', 'legacy environment mismatch']) {
  test(`policy review with ${damage} cleans owned credential files and remains retryable`, t => {
    const f = fixture(t);
    const initial = f.run('browser');
    assert.equal(initial.status, 0, initial.stderr);
    const active = readJson(f.pointer);
    const file = path.join(active.directory, 'review-context.json');
    const original = fs.readFileSync(file, 'utf8');
    const context = JSON.parse(original);
    if (damage === 'missing Compose arguments') delete context.args;
    if (damage === 'legacy environment mismatch') {
      fs.rmSync(path.join(active.directory, 'owner.json'), {force: true});
      context.environment.WEBAPI_E2E_DIRECTORY = 'foreign';
    }
    const damaged = damage === 'unreadable context' ? '{' : JSON.stringify(context);
    fs.writeFileSync(file, damaged);
    const failure = f.run('cleanup');
    assert.notEqual(failure.status, 0);
    assert.deepEqual(remaining(active.directory), []);
    assert.equal(fs.existsSync(path.join(active.directory, 'down-attempts')), false);
    assert.equal(fs.readFileSync(file, 'utf8'), damaged);
    assert.deepEqual(readJson(f.pointer), active);
    const receipt = readJson(path.join(f.evidence, 'cleanup.json'));
    assert.equal(receipt.failed, true);
    assert.equal(receipt.secretsRemaining, 0);
    assert.notEqual(receipt.containersRemaining, 0);
    fs.writeFileSync(file, original);
    const retry = f.run('cleanup');
    assert.equal(retry.status, 0, retry.stderr);
    assert.equal(fs.existsSync(f.pointer), false);
  });
}

test('policy review teardown preserves a different pointer installed during Docker down', t => {
  const f = fixture(t);
  const initial = f.run('browser');
  assert.equal(initial.status, 0, initial.stderr);
  const {directory, project} = readJson(f.pointer);
  const replacement = {directory: path.join(path.dirname(directory), 'unrelated'), project: 'different-project'};
  const cleanup = f.run('cleanup', {FIXTURE_SWITCH_POINTER: JSON.stringify(replacement)});
  assert.equal(cleanup.status, 0, cleanup.stderr);
  assert.deepEqual(remaining(directory), []);
  assert.deepEqual(readJson(f.pointer), replacement);
  assert.equal(readJson(path.join(f.evidence, 'cleanup.json')).project, project);
});

test('closing a policy preview after a handoff preserves the active review', async t => {
  const f = fixture(t);
  const initial = f.run('browser');
  assert.equal(initial.status, 0, initial.stderr);
  const {directory} = readJson(f.pointer);
  // The pointer update is the behavior under test. No listener or real service is needed.
  const mockHttp = path.join(f.root, 'preview-http.cjs');
  fs.writeFileSync(mockHttp, `
    require('node:http').createServer = () => ({
      listen(port, host, ready) { this.keepAlive = setInterval(() => {}, 60000); ready(); },
      address() { return {port: 12345}; },
      close(callback) { clearInterval(this.keepAlive); callback(); }
    });
  `);
  const child = spawn(process.execPath, ['--require', mockHttp, 'scripts/policies/preview.mjs'], {cwd: f.root});
  t.after(() => child.kill());
  const exit = once(child, 'exit');
  const ready = await Promise.race([once(child.stdout, 'data').then(() => true), exit.then(() => false)]);
  assert.equal(ready, true);
  const replacement = {directory: path.join(path.dirname(directory), 'unrelated'), project: 'different-project'};
  fs.writeFileSync(f.pointer, JSON.stringify(replacement));
  child.kill('SIGTERM');
  assert.deepEqual(await exit, [0, null]);
  assert.deepEqual(readJson(f.pointer), replacement);
});
