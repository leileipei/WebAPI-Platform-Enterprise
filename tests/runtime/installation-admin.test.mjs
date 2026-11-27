import test from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {withInstallationAdmin} from './integration/fixture.mjs';

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
