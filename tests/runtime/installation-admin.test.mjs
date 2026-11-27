import test from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {spawn} from 'node:child_process';
import {once} from 'node:events';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {initializeState, writePrivate} from '../../scripts/runtime/state.mjs';
import {withInstallationAdmin, installationCleanupGuard, removeInstallationFixture} from './integration/fixture.mjs';

for (const outcome of ['success', 'scenario failure', 'rotation failure']) {
  test('installation admin removes its private password after ' + outcome, async t => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'installation-admin-'));
    t.after(() => fs.rm(root, {recursive:true, force:true}));
    const projectName = 'webapi-enterprise-local-test-' + randomUUID();
    const directory = path.join(root, projectName);
    const secrets = path.join(directory, 'secrets');
    await fs.mkdir(directory, {mode:0o700});
    await fs.mkdir(secrets, {mode:0o700});
    const bootstrap = randomUUID();
    await fs.writeFile(path.join(secrets, 'bootstrap-password'), bootstrap, {mode:0o600});
    let replacement, used = false;
    const client = {
      async login(username, password) {
        assert.equal(username, 'admin');
        assert.ok(password === bootstrap);
        return {id:'admin-id', mustChangePassword:true};
      },
      async request(url, options) {
        assert.equal(url, '/auth/change-password');
        assert.equal(options.method, 'POST');
        assert.ok(options.body.currentPassword === bootstrap);
        replacement = options.body.newPassword;
        assert.ok(replacement && replacement !== bootstrap && replacement === options.body.confirmPassword);
        if (outcome === 'rotation failure') throw Error(outcome);
        return {id:'admin-id', mustChangePassword:false};
      }
    };
    const run = withInstallationAdmin(directory, {projectName, bootstrapUsername:'admin'}, client, async passwordFile => {
      used = true;
      assert.equal((await fs.stat(passwordFile)).mode & 0o777, 0o600);
      assert.ok(await fs.readFile(passwordFile, 'utf8') === replacement);
      assert.notEqual(path.dirname(passwordFile), secrets);
      if (outcome === 'scenario failure') throw Error(outcome);
    });
    if (outcome === 'success') await run;
    else await assert.rejects(run, {message:outcome});
    assert.equal(used, outcome !== 'rotation failure');
    assert.deepEqual(await fs.readdir(directory), ['secrets']);
    assert.deepEqual(await fs.readdir(secrets), ['bootstrap-password']);
    assert.ok(await fs.readFile(path.join(secrets, 'bootstrap-password'), 'utf8') === bootstrap);
  });
}

test('installation admin revokes its inode and preserves a replaced file and the primary error', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'installation-admin-replaced-'));
  t.after(() => fs.rm(root, {recursive:true, force:true}));
  const projectName = 'webapi-enterprise-local-test-' + randomUUID();
  const directory = path.join(root, projectName);
  await fs.mkdir(directory, {mode:0o700});
  await fs.mkdir(path.join(directory, 'secrets'), {mode:0o700});
  await fs.writeFile(path.join(directory, 'secrets/bootstrap-password'), randomUUID(), {mode:0o600});
  const client = {
    login:async () => ({id:'admin', mustChangePassword:true}),
    request:async () => ({id:'admin', mustChangePassword:false})
  };
  const primary = Error('scenario failed');
  const renamed = path.join(directory, 'renamed');
  await assert.rejects(withInstallationAdmin(directory, {projectName, bootstrapUsername:'admin'}, client, async file => {
    await fs.rename(file, renamed);
    await fs.writeFile(file, 'replacement', {mode:0o600, flag:'wx'});
    throw primary;
  }), error => error === primary && /replaced/.test(error.cleanupError.message));
  assert.equal((await fs.stat(renamed)).size, 0);
  assert.equal(await fs.readFile(path.join(directory, 'installation-admin.password'), 'utf8'), 'replacement');
});

for (const changed of ['owner', 'directory']) {
  test('successful installation retains its resources when the original ' + changed + ' changes', async t => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'installation-ownership-'));
    t.after(() => fs.rm(root, {recursive:true, force:true}));
    const projectName = 'webapi-enterprise-local-test-' + randomUUID();
    const directory = path.join(root, projectName);
    const initial = await initializeState(directory, {projectName, bootstrapUsername:'fixture-admin'});
    const recheck = await installationCleanupGuard(directory, initial);
    assert.equal((await recheck()).ownerId, initial.ownerId);
    if (changed === 'directory') {
      await fs.rename(directory, directory + '-moved');
      await fs.cp(directory + '-moved', directory, {recursive:true});
    } else {
      const ownerId = randomUUID();
      await writePrivate(path.join(directory, 'owner.json'), {schemaVersion:1, projectName, ownerId});
      await writePrivate(path.join(directory, 'runtime.json'), {...initial, ownerId});
    }
    let teardownCalled = false;
    await assert.rejects(removeInstallationFixture(directory, recheck, () => { teardownCalled = true; }, true),
      changed === 'directory' ? /directory identity changed/ : /owner identity changed/);
    assert.equal(teardownCalled, false);
    assert.ok((await fs.stat(directory)).isDirectory());
  });
}

for (const interruption of ['SIGTERM', 'SIGINT', 'SIGHUP', 'exit']) {
  test('installation admin revokes its open credential on ' + interruption, async t => {
    const root = await fs.mkdtemp(path.join(os.tmpdir(), 'installation-admin-exit-'));
    t.after(() => fs.rm(root, {recursive:true, force:true}));
    const projectName = 'webapi-enterprise-local-test-' + randomUUID();
    const directory = path.join(root, projectName);
    await fs.mkdir(directory, {mode:0o700});
    await fs.mkdir(path.join(directory, 'secrets'), {mode:0o700});
    await fs.writeFile(path.join(directory, 'secrets/bootstrap-password'), randomUUID(), {mode:0o600});
    const fixtureUrl = new URL('./integration/fixture.mjs', import.meta.url).href;
    const program = [
      'import {withInstallationAdmin} from ' + JSON.stringify(fixtureUrl),
      'const [directory, projectName] = process.argv.slice(1)',
      "const client = {login:async () => ({id:'admin', mustChangePassword:true}), request:async () => ({id:'admin', mustChangePassword:false})}",
      'await withInstallationAdmin(directory, {projectName, bootstrapUsername:"admin"}, client, async () => {',
      '  process.on("message", () => process.exit(23))',
      '  process.send("credential ready")',
      '  await new Promise(() => {})',
      '})'
    ].join('\n');
    const child = spawn(process.execPath, ['--input-type=module', '-e', program, directory, projectName],
      {stdio:['ignore', 'ignore', 'inherit', 'ipc']});
    const exited = once(child, 'exit');
    t.after(async () => {
      if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); await exited; }
    });
    const [message] = await Promise.race([once(child, 'message', {signal:AbortSignal.timeout(10000)}),
      exited.then(() => { throw Error('Child exited before opening its fixture credential'); })]);
    assert.equal(message, 'credential ready');
    const file = path.join(directory, 'installation-admin.password');
    const original = await fs.open(file, 'r');
    try {
      assert.ok((await original.stat()).size > 0);
      if (interruption === 'exit') child.send('exit');
      else child.kill(interruption);
      const [code, signal] = await exited;
      if (interruption === 'exit') assert.equal(code, 23);
      else assert.equal(signal, interruption);
      assert.equal((await original.stat()).size, 0);
      await assert.rejects(fs.access(file), {code:'ENOENT'});
    } finally {
      await original.close();
    }
  });
}
