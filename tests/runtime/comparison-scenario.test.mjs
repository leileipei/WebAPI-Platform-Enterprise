import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {test} from 'node:test';
import {runComparisonScenario} from '../../scripts/comparisons/scenario.mjs';

test('comparison actors use their prepared passwords and the new risk reviewer completes first login', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'comparison-actors-'));
  t.after(() => fs.rm(directory, {recursive:true, force:true}));
  const passwordFiles = {}, passwords = {};
  await fs.writeFile(path.join(directory, 'password'), 'fixture-original', {mode:0o600});
  for (const user of ['e2e-admin', 'e2e-approver', 'e2e-security']) {
    passwords[user] = 'rotated-' + user;
    passwordFiles[user] = '/workspace/.runtime/' + path.basename(directory) + '/password-' + user;
    await fs.writeFile(path.join(directory, 'password-' + user), passwords[user], {mode:0o600});
  }
  const login = {}, changes = [], versions = [];
  const reached = new Error('comparison inputs reached');
  t.mock.method(globalThis, 'fetch', async (url, options) => {
    const route = new URL(url).pathname.slice('/api/v1'.length);
    const body = options.body && JSON.parse(options.body);
    if (route === '/auth/csrf') return Response.json({token:'fixture'});
    if (route === '/auth/login') {
      login[body.username] = body.password;
      return Response.json({id:body.username, mustChangePassword:body.username === 'e2e-risk'});
    }
    if (route === '/auth/change-password') {
      changes.push(body);
      return Response.json({id:'e2e-risk', mustChangePassword:false});
    }
    if (route.endsWith('/versions')) {
      versions.push(body);
      return Response.json({id:body.version, revision:1});
    }
    if (route.endsWith('/version-comparisons')) throw reached;
    return Response.json({id:'fixture', revision:1});
  });
  await assert.rejects(runComparisonScenario({directory, endpoints:{control_plane:'http://127.0.0.1:1'},
    context:{passwordFiles, apiId:'fixture'}}), error => error === reached);
  assert.deepEqual(login, {...passwords, 'e2e-risk':'fixture-original'});
  assert.equal(changes.length, 1);
  assert.equal(changes[0].currentPassword, 'fixture-original');
  assert.notEqual(changes[0].newPassword, changes[0].currentPassword);
  assert.equal(changes[0].newPassword, changes[0].confirmPassword);
  assert.deepEqual(versions.map(v => {
    const document = JSON.parse(v.openapiDocument);
    return [v.version, Boolean(document.info?.title), document.info?.version, document.paths['/orders'].get.parameters[0].required];
  }), [['1.0.0', true, '1.0.0', false], ['2.0.0', true, '2.0.0', true]]);
});
