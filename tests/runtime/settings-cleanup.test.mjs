import assert from 'node:assert/strict';
import {spawn, spawnSync} from 'node:child_process';
import {once} from 'node:events';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {runSettingsAcceptance} from '../../scripts/settings/acceptance.mjs';

const names = [
  'password', 'postgres-password', 'node-a', 'node-b', 'ip-hmac', 'cursor-key', 'login-hmac',
  'credential', 'password-e2e-admin', 'password-e2e-reader', 'password-e2e-approver',
  'password-e2e-security', 'original.snapshot', 'rollback.snapshot',
];
const canary = 'synthetic-settings-private-value';
const read = filename => JSON.parse(fs.readFileSync(filename, 'utf8'));
const write = (filename, data) => fs.writeFileSync(filename, JSON.stringify(data), {mode:0o600});
const remaining = directory => names.filter(name => fs.existsSync(path.join(directory, name)));
const publicDir = root => path.join(root, 'docs/evidence/settings');
const pointer = root => path.join(root, '.runtime/settings-review.json');

function fixture(t) {
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'settings-cleanup-')));
  t.after(() => fs.rmSync(root, {recursive:true, force:true}));
  fs.mkdirSync(publicDir(root), {recursive:true});
  return root;
}

function addScripts(root) {
  fs.cpSync(new URL('../../scripts/', import.meta.url), path.join(root, 'scripts'), {recursive:true});
  const bin = path.join(root, 'bin');
  fs.mkdirSync(bin);
  // The runner and cleanup CLI still execute normally. Docker and the SDK inputs are synthetic.
  fs.writeFileSync(path.join(bin, 'docker'), `#!${process.execPath}
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const a = process.argv.slice(2), project = process.env.WEBAPI_E2E_PROJECT;
const dir = process.env.WEBAPI_E2E_DIRECTORY, owner = project.slice('webapi-enterprise-e2e-settings-'.length);
const mode = process.env.SETTINGS_DOCKER_RESULT || '';
if (a[0] === 'ps' || a[0] === 'volume' && a[1] === 'ls') {
  if (mode === 'unavailable') process.exit(24);
  if (a[0] === 'volume' && fs.existsSync(path.join(dir,'fixture-volume-present'))) {
    const filter = a.at(-1), exact = project+'_evidence';
    if (filter.startsWith('name=') || mode !== 'unlabelled-existing' && filter === 'label=com.webapi.settings.owner='+owner) console.log(exact);
  }
  if (a[0] === 'ps' && mode === 'foreign') console.log('foreign');
  if (a[0] === 'ps' && mode === 'leftover') console.log('owned');
} else if (a[0] === 'inspect' || a[0] === 'volume' && a[1] === 'inspect') {
  if (a[0] === 'volume' && a.at(-1) === project+'_evidence' && fs.existsSync(path.join(dir,'fixture-volume-present'))) {
    if (mode === 'unlabelled-existing') console.log('{}');
    else if (mode === 'foreign-project' || mode === 'relabel-after-down' && fs.existsSync(path.join(dir,'fixture-down-called')))
      console.log(JSON.stringify({'com.docker.compose.project':'another-project','com.webapi.settings.owner':owner}));
    else console.log(JSON.stringify({'com.webapi.settings.owner':owner}));
  } else console.log(JSON.stringify({'com.docker.compose.project':project,'com.webapi.settings.owner':mode === 'foreign' ? 'another-owner' : owner}));
} else if (a[0] === 'volume' && a[1] === 'rm' && a.length === 3 && a[2] === project+'_evidence') {
  fs.unlinkSync(path.join(dir,'fixture-volume-present'));
} else if (a[0] === 'compose' && a.includes('down')) {
  fs.appendFileSync(path.join(dir, 'fixture-down-called'), 'down' + String.fromCharCode(10));
  if (mode === 'interrupt-down') setTimeout(() => process.exit(0), 750);
  if (mode === 'fail-down') { console.error('${canary}'); process.exit(25); }
  if (mode === 'new-pointer') fs.copyFileSync(path.join(dir, 'replacement-pointer.json'), path.join(dir, '../settings-review.json'));
} else if (a[0] === 'compose' && a.includes('--force-recreate') && mode === 'interrupt-up') {
  fs.writeFileSync(path.join(dir, 'fixture-up-ready'), '');
  setTimeout(() => process.exit(0), 2500);
} else if (a[0] === 'compose' && a.includes('port')) {
  console.log('127.0.0.1:1');
} else if (a[0] === 'image' && a[1] === 'inspect') {
  const hash = crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, 'build/source-manifest.json'))).digest('hex');
  console.log(JSON.stringify([{Id:'synthetic-image',Config:{Labels:{'com.webapi.settings.source-manifest':hash}}}]));
} else if (a[0] === 'build' || a[0] === 'run' || a[0] === 'volume' && a[1] === 'create'
  || a[0] === 'compose' && a.some(arg => ['up','run','logs','create'].includes(arg))) {
  process.exit(0);
} else process.exit(80);
`, {mode:0o700});
}

function cli(root, args, mode, extra = {}) {
  return spawnSync(process.execPath, args, {
    cwd:root, encoding:'utf8', timeout:20000,
    env:{...process.env, PATH:path.join(root, 'bin') + path.delimiter + process.env.PATH,
      SETTINGS_DOCKER_RESULT:mode || '', ...extra},
  });
}

function addReview(root) {
  const owner = randomUUID(), project = 'webapi-enterprise-e2e-settings-' + owner;
  const directory = path.join(root, '.runtime', project);
  fs.mkdirSync(directory, {recursive:true, mode:0o700});
  for (const name of names) fs.writeFileSync(path.join(directory, name), canary);
  fs.writeFileSync(path.join(directory, 'password-e2e-unrelated'), 'keep');
  const info = {directory, project};
  write(path.join(directory, 'context.json'), {environmentId:'fixture'});
  const composeFiles = ['build/source/deploy/compose.e2e.yml','build/source/deploy/compose.observability.yml',
    'build/source/deploy/compose.observability-e2e.yml','settings-overlay.yml','settings-volumes.yml'];
  const args = ['compose','-p',project];
  for (const name of composeFiles) {
    const file = path.join(directory, name);
    fs.mkdirSync(path.dirname(file), {recursive:true});
    write(file, {services:{}});
    args.push('-f', file);
  }
  write(path.join(directory, 'review-context.json'), {...info, owner, args,
    environment:{WEBAPI_E2E_PROJECT:project, WEBAPI_E2E_DIRECTORY:directory}});
  write(pointer(root), info);
  return info;
}

function addBuild(root) {
  addScripts(root);
  fs.writeFileSync(path.join(root, 'bin/git'), '#!/bin/sh\n[ "$*" = "rev-parse HEAD" ] || exit 80\nprintf "%040d\\n" 0\n', {mode:0o700});
  for (const folder of ['deploy','console/node_modules/typescript/bin','console/node_modules/vite/bin']) {
    fs.mkdirSync(path.join(root, folder), {recursive:true});
  }
  write(path.join(root, 'deploy/images.lock.json'), {sdk:'fixture'});
  fs.writeFileSync(path.join(root, 'console/node_modules/typescript/bin/tsc'), '');
  fs.writeFileSync(path.join(root, 'console/node_modules/vite/bin/vite.js'), "require('node:fs').mkdirSync('dist')");
  for (const name of ['wait-http.mjs','wait-nodes.mjs']) fs.writeFileSync(path.join(root, 'scripts', name), '');
  fs.writeFileSync(path.join(root, 'scripts/prepare-e2e.mjs'), `
    import fs from 'node:fs';
    import path from 'node:path';
    const dir=process.env.WEBAPI_E2E_DIRECTORY;
    for(const name of ${JSON.stringify(names.slice(7))}) fs.writeFileSync(path.join(dir,name),'${canary}');
    fs.writeFileSync(path.join(dir,'password-e2e-unrelated'),'keep');
    fs.writeFileSync(path.join(dir,'context.json'),JSON.stringify({environmentId:'fixture'}));
    if(process.env.SETTINGS_DOCKER_RESULT==='interrupt-writer') {
      process.on('SIGTERM',()=>setTimeout(()=>{
        fs.writeFileSync(path.join(dir,'password-e2e-admin'),'late-fixture-placeholder');
        fs.writeFileSync(path.join(dir,'fixture-writer-closed'),'');
        process.exit(0);
      }, 850));
      fs.writeFileSync(path.join(dir,'fixture-prepare-wait'),'');
      setTimeout(()=>process.exit(7),6000);
    }
  `);
  fs.writeFileSync(path.join(root, 'scripts/settings/scenario.mjs'), `
    export async function runSettingsScenario(ctx) {
      if(process.env.SETTINGS_SCENARIO_FAIL==='true') throw Error('synthetic scenario failure');
      return {project:ctx.project,checks:[]};
    }
  `);
}

for (const phase of ['during credential generation', 'before Compose']) {
  test('settings acceptance removes files and reports the primary failure ' + phase, async t => {
    const root = fixture(t);
    if (phase === 'during credential generation') {
      const writeFile = fsp.writeFile;
      t.mock.method(fsp, 'writeFile', async (file, ...rest) => {
        if (path.basename(file) === 'node-b') throw Error('synthetic credential generation failure');
        return writeFile(file, ...rest);
      });
    }
    await assert.rejects(runSettingsAcceptance({root}), phase === 'before Compose' ? /ENOENT/ : /synthetic credential generation failure/);
    const [project] = fs.readdirSync(path.join(root, '.runtime')).filter(name => name.startsWith('webapi-'));
    assert.deepEqual(remaining(path.join(root, '.runtime', project)), []);
    const failure = read(path.join(publicDir(root), 'last-failure.json'));
    assert.equal(failure.project, project);
    assert.equal(failure.complete, false);
    assert.equal(failure.cleanup.secretsRemaining, 0);
    assert.ok(failure.cleanup.containersRemaining == null && failure.cleanup.volumesRemaining == null);
  });
}

for (const phase of ['success','scenario and teardown failure','cleanup failure after scenario success']) {
  test('settings acceptance measures credentials after Compose on ' + phase, t => {
    const root = fixture(t);
    addBuild(root);
    const failed = phase !== 'success';
    const r = cli(root, ['scripts/settings/acceptance.mjs'], failed ? 'fail-down' : '', {SETTINGS_SCENARIO_FAIL:String(phase === 'scenario and teardown failure')});
    if (failed) {
      assert.notEqual(r.status, 0);
      assert.match(r.stderr, phase === 'scenario and teardown failure' ? /scenario failure|failed during settings scenario/ : /Settings Docker cleanup incomplete/);
    } else assert.equal(r.status, 0, r.stderr);
    const receipt = read(path.join(publicDir(root), failed ? 'last-failure.json' : 'verification.json'));
    const directory = path.join(root, '.runtime', receipt.project);
    assert.equal(receipt.complete, !failed);
    assert.deepEqual(remaining(directory), []);
    assert.ok(fs.existsSync(path.join(directory, 'password-e2e-unrelated')));
    assert.equal(fs.readFileSync(path.join(directory, 'fixture-down-called'), 'utf8'), 'down\n');
    assert.equal(receipt.cleanup.secretsRemaining, 0);
    assert.equal(receipt.cleanup.containersRemaining, 0);
    assert.equal(receipt.cleanup.volumesRemaining, 0);
    if (failed) {
      assert.equal(receipt.cleanup.failed, true);
      assert.ok(!JSON.stringify(receipt).includes(canary));
      assert.ok(fs.existsSync(path.join(directory, 'review-context.json')));
      if (phase === 'cleanup failure after scenario success') {
        const proof = read(path.join(publicDir(root), 'verification.json'));
        assert.equal(proof.complete, false);
        assert.equal(proof.scenarioComplete, true);
        assert.equal(proof.cleanup.failed, true);
      }
    }
  });
}

for (const mode of ['success','fail-down','unavailable','leftover','foreign','secret-stuck']) {
  test('settings review cleanup measures files and preserves retry state on ' + mode, t => {
    const root = fixture(t);
    addScripts(root);
    const {directory, project} = addReview(root);
    const verification = path.join(publicDir(root), 'verification.json');
    write(verification, {project, complete:mode !== 'leftover', checks:[{name:'preserve this finding'}]});
    if (mode === 'secret-stuck') {
      fs.unlinkSync(path.join(directory, 'password-e2e-reader'));
      fs.mkdirSync(path.join(directory, 'password-e2e-reader'));
    }
    const r = cli(root, ['scripts/settings/cleanup.mjs'], mode);
    const failed = mode !== 'success';
    if (failed) assert.notEqual(r.status, 0);
    else assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(remaining(directory), mode === 'secret-stuck' ? ['password-e2e-reader'] : []);
    const receipt = read(path.join(publicDir(root), 'cleanup.json'));
    assert.equal(receipt.project, project);
    assert.equal(receipt.secretsRemaining, mode === 'secret-stuck' ? 1 : 0);
    assert.equal(Boolean(receipt.failed), failed);
    if (mode === 'unavailable') assert.ok(receipt.containersRemaining == null && receipt.volumesRemaining == null);
    if (mode === 'leftover') assert.equal(receipt.containersRemaining, 1);
    if (mode === 'foreign') assert.equal(fs.existsSync(path.join(directory, 'fixture-down-called')), false);
    assert.ok(!JSON.stringify(receipt).includes(canary));
    assert.equal(fs.existsSync(pointer(root)), failed);
    assert.equal(fs.existsSync(path.join(directory, 'review-context.json')), failed);
    assert.equal(fs.existsSync(path.join(directory, 'password-e2e-unrelated')), true);
    assert.equal(read(verification).complete, !failed);
    if (failed) {
      if (mode === 'secret-stuck') fs.rmdirSync(path.join(directory, 'password-e2e-reader'));
      const retry = cli(root, ['scripts/settings/cleanup.mjs']);
      assert.equal(retry.status, 0, retry.stderr);
      assert.equal(read(verification).complete, mode !== 'leftover');
      assert.equal(read(verification).cleanup.secretsRemaining, 0);
      assert.equal(fs.existsSync(pointer(root)), false);
    }
  });
}

test('review cleanup leaves another project pointer and verification intact', t => {
  const root = fixture(t);
  addScripts(root);
  const {directory} = addReview(root);
  const newer = {project:'another-review',directory:path.join(root, '.runtime', 'another-review')};
  const verification = path.join(publicDir(root), 'verification.json');
  write(verification, {...newer, complete:true});
  write(path.join(directory, 'replacement-pointer.json'), newer);
  const r = cli(root, ['scripts/settings/cleanup.mjs'], 'new-pointer');
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(read(pointer(root)), newer);
  assert.deepEqual(read(verification), {...newer, complete:true});
});

test('review cleanup rejects a mismatched pointer before touching files', t => {
  const root = fixture(t);
  addScripts(root);
  const {directory, project} = addReview(root);
  write(pointer(root), {project:project + '-wrong', directory});
  const r = cli(root, ['scripts/settings/cleanup.mjs']);
  assert.notEqual(r.status, 0);
  assert.deepEqual(remaining(directory), names);
  assert.equal(fs.existsSync(path.join(directory, 'fixture-down-called')), false);
});

test('retained settings review keeps owned credentials until measured cleanup', t => {
  const root = fixture(t);
  addBuild(root);
  const start = cli(root, ['scripts/settings/acceptance.mjs', '--retain-for-review']);
  assert.equal(start.status, 0, start.stderr);
  const current = read(pointer(root)), verification = path.join(publicDir(root), 'verification.json');
  assert.equal(read(verification).review.project, current.project);
  assert.equal(read(verification).cleanup, undefined);
  assert.deepEqual(remaining(current.directory), names);
  const stop = cli(root, ['scripts/settings/cleanup.mjs']);
  assert.equal(stop.status, 0, stop.stderr);
  assert.deepEqual(remaining(current.directory), []);
  assert.equal(read(verification).cleanup.secretsRemaining, 0);
  assert.equal(fs.existsSync(pointer(root)), false);
});

test('a second acceptance cannot replace an active settings review', t => {
  const root = fixture(t);
  addBuild(root);
  const active = addReview(root);
  const start = cli(root, ['scripts/settings/acceptance.mjs', '--retain-for-review']);
  assert.notEqual(start.status, 0);
  assert.deepEqual(read(pointer(root)), active);
  assert.deepEqual(remaining(active.directory), names);
  const failed = read(path.join(publicDir(root), 'last-failure.json'));
  assert.notEqual(failed.project, active.project);
  assert.equal(failed.cleanup.secretsRemaining, 0);
});

test('an evidence write failure preserves the primary acceptance error and the previous failure history', async t => {
  const root = fixture(t), history = path.join(publicDir(root), 'failed-attempts.json');
  fs.writeFileSync(history, 'previous interrupted history');
  await assert.rejects(runSettingsAcceptance({root}), error => error.code === 'ENOENT');
  assert.equal(fs.readFileSync(history, 'utf8'), 'previous interrupted history');
  assert.equal(read(path.join(publicDir(root), 'last-failure.json')).cleanup.secretsRemaining, 0);
});

test('an indirect settings review pointer cannot authorize cleanup', t => {
  const root = fixture(t);
  addScripts(root);
  const active = addReview(root);
  const backup = path.join(active.directory, 'indirect-pointer.json');
  fs.renameSync(pointer(root), backup);
  fs.symlinkSync(backup, pointer(root));
  const result = cli(root, ['scripts/settings/cleanup.mjs']);
  assert.notEqual(result.status, 0);
  assert.deepEqual(remaining(active.directory), names);
});

for (const damage of ['unreadable review context', 'invalid Compose project', 'missing Compose arguments','truncated Compose arguments','an unrelated Compose overlay']) {
  test('settings cleanup measures credentials after ownership is confirmed despite ' + damage, t => {
    const root = fixture(t);
    addScripts(root);
    const {directory, project} = addReview(root), owner = project.slice('webapi-enterprise-e2e-settings-'.length);
    const file = path.join(directory, 'review-context.json'), original = read(file);
    if (damage === 'unreadable review context') {
      write(path.join(directory, 'owner.json'), {project, owner});
      fs.writeFileSync(file, '{');
    } else {
      const candidates = {
        'invalid Compose project':['compose','-p','other-project'],
        'missing Compose arguments':null,
        'truncated Compose arguments':original.args.slice(0, 3),
        'an unrelated Compose overlay':[...original.args.slice(0, -1), path.join(directory, 'other.yml')],
      };
      write(file, {...original, args:candidates[damage]});
    }
    const failed = cli(root, ['scripts/settings/cleanup.mjs']);
    assert.notEqual(failed.status, 0);
    assert.deepEqual(remaining(directory), []);
    assert.equal(fs.existsSync(path.join(directory, 'fixture-down-called')), false);
    assert.ok(fs.existsSync(file) && fs.existsSync(pointer(root)));
    const receipt = read(path.join(publicDir(root), 'cleanup.json'));
    assert.equal(receipt.failed, true);
    assert.equal(receipt.secretsRemaining, 0);
    assert.ok(receipt.containersRemaining == null && receipt.volumesRemaining == null);
    write(file, original);
    assert.equal(cli(root, ['scripts/settings/cleanup.mjs']).status, 0);
    assert.equal(fs.existsSync(pointer(root)), false);
  });
}

test('conflicting owner records cannot authorize settings cleanup', t => {
  const root = fixture(t);
  addScripts(root);
  const {directory, project} = addReview(root);
  write(path.join(directory, 'owner.json'), {project, owner:randomUUID()});
  const result = cli(root, ['scripts/settings/cleanup.mjs']);
  assert.notEqual(result.status, 0);
  assert.deepEqual(remaining(directory), names);
});

test('a legacy run with unreadable ownership evidence keeps all files', t => {
  const root = fixture(t);
  addScripts(root);
  const {directory} = addReview(root);
  fs.writeFileSync(path.join(directory, 'review-context.json'), '{');
  assert.notEqual(cli(root, ['scripts/settings/cleanup.mjs']).status, 0);
  assert.deepEqual(remaining(directory), names);
});

for (const kind of ['acceptance', 'review cleanup', 'fixture writer']) {
  test('the settings ' + kind + ' CLI completes credential cleanup after a process signal', async t => {
    const root = fixture(t), accept = kind !== 'review cleanup';
    if (accept) addBuild(root);
    else addScripts(root);
    if (!accept) addReview(root);
    const child = spawn(process.execPath, ['scripts/settings/' + (accept ? 'acceptance' : 'cleanup') + '.mjs'], {
      cwd:root, stdio:'ignore', env:{...process.env, PATH:path.join(root, 'bin') + path.delimiter + process.env.PATH,
        SETTINGS_DOCKER_RESULT:kind === 'fixture writer' ? 'interrupt-writer' : accept ? 'interrupt-up' : 'interrupt-down'},
    });
    const exited = once(child, 'exit');
    t.after(() => { if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL'); });
    const runtime = path.join(root, '.runtime'), deadline = Date.now() + 15000;
    let directory;
    for (;;) {
      if (fs.existsSync(runtime)) {
        const project = fs.readdirSync(runtime).find(name => name.startsWith('webapi-enterprise-e2e-settings-'));
        if (project) {
          directory = path.join(runtime, project);
          const ready = kind === 'fixture writer' ? 'fixture-prepare-wait' : accept ? 'fixture-up-ready' : 'fixture-down-called';
          if (fs.existsSync(path.join(directory, ready))) break;
        }
      }
      assert.ok(Date.now() < deadline && child.exitCode === null && child.signalCode === null, 'CLI did not reach the signal point');
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    assert.deepEqual(remaining(directory), names);
    child.kill(accept ? 'SIGINT' : 'SIGTERM');
    assert.deepEqual(await exited, [accept ? 130 : 143, null]);
    if (kind === 'fixture writer') {
      const end = Date.now() + 3000;
      while (!fs.existsSync(path.join(directory, 'fixture-writer-closed'))) {
        assert.ok(Date.now() < end, 'interrupted fixture writer failed to finish');
        await new Promise(resolve => setTimeout(resolve, 25));
      }
    }
    assert.deepEqual(remaining(directory), []);
    const record = read(path.join(publicDir(root), accept ? 'last-failure.json' : 'cleanup.json'));
    if (accept) assert.equal(record.complete, false);
    const receipt = accept ? record.cleanup : record;
    assert.equal(receipt.secretsRemaining, 0);
    assert.equal(receipt.containersRemaining, 0);
    assert.equal(receipt.volumesRemaining, 0);
    assert.equal(Boolean(receipt.failed), false);
  });
}

test('cancelling a settings scenario reaches its in-flight control-plane request', async t => {
  const root = fixture(t), workspace = addReview(root), controller = new AbortController();
  const reason = Error('cancel this settings run');
  const {runSettingsScenario} = await import('../../scripts/settings/scenario.mjs');
  t.mock.method(globalThis, 'fetch', async (_url, options) => {
    controller.abort(reason);
    if (!options.signal) throw Error('Settings request remained active');
    options.signal.throwIfAborted();
  });
  await assert.rejects(runSettingsScenario({...workspace, root, signal:controller.signal,
    endpoints:{control_plane:'http://127.0.0.1:1'}, context:{passwordFiles:{}}}), error => error === reason);
});

for (const mode of ['legacy-evidence','foreign-project','unlabelled-existing','relabel-after-down']) {
  test('settings teardown protects the exact manually created volume on ' + mode, t => {
    const root = fixture(t);
    addScripts(root);
    const {directory} = addReview(root), volume = path.join(directory, 'fixture-volume-present');
    fs.writeFileSync(volume, '');
    const result = cli(root, ['scripts/settings/cleanup.mjs'], mode);
    if (mode === 'legacy-evidence') assert.equal(result.status, 0, result.stderr);
    else assert.notEqual(result.status, 0);
    assert.equal(fs.existsSync(volume), mode !== 'legacy-evidence');
    assert.equal(fs.existsSync(path.join(directory, 'fixture-down-called')), ['legacy-evidence','relabel-after-down'].includes(mode));
    const receipt = read(path.join(publicDir(root), 'cleanup.json'));
    assert.equal(receipt.volumesRemaining, mode === 'legacy-evidence' ? 0 : 1);
    assert.equal(Boolean(receipt.failed), mode !== 'legacy-evidence');
    assert.equal(receipt.secretsRemaining, 0);
    assert.deepEqual(remaining(directory), []);
  });
}

test('an independent owner cannot override an explicitly conflicting settings teardown identity', t => {
  const root = fixture(t);
  addScripts(root);
  const {directory, project} = addReview(root), file = path.join(directory, 'review-context.json');
  const trusted = read(file);
  write(path.join(directory, 'owner.json'), {project, owner:trusted.owner});
  write(file, {...trusted, environment:{...trusted.environment, WEBAPI_E2E_PROJECT:'another-settings-project'}});
  const result = cli(root, ['scripts/settings/cleanup.mjs']);
  assert.notEqual(result.status, 0);
  assert.deepEqual(remaining(directory), names);
  assert.equal(fs.existsSync(path.join(directory, 'fixture-down-called')), false);
  assert.ok(fs.existsSync(pointer(root)));
});
