import assert from 'node:assert/strict';
import {execFileSync, spawnSync} from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

const actors = ['admin', 'reader', 'approver', 'security'];
const bootstrapFiles = ['password', 'postgres-password', 'node-a', 'node-b', 'login-hmac'];

function runFixture(t, {rotated = actors.length, runExit = 0, downExit = 0}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'webapi-core-cleanup-'));
  t.after(() => fs.rmSync(root, {recursive: true, force: true}));
  const scripts = path.join(root, 'scripts');
  const bin = path.join(root, 'bin');
  const fixtures = path.join(root, '.runtime', 'e2e');
  const other = path.join(fixtures, 'other-project');
  for (const dir of [scripts, bin, other, path.join(root, 'console', 'dist')]) {
    fs.mkdirSync(dir, {recursive: true});
  }
  fs.writeFileSync(path.join(other, 'password-e2e-admin'), 'other fixture');
  fs.writeFileSync(path.join(root, 'console', 'dist', 'index.html'), '');
  fs.copyFileSync(new URL('../../scripts/e2e.sh', import.meta.url), path.join(scripts, 'e2e.sh'));
  fs.writeFileSync(path.join(bin, 'docker'), `#!/bin/sh
if [ "$4" = down ]; then
  touch "$WEBAPI_E2E_DIRECTORY/down-attempted"
  exit "$FIXTURE_DOWN_EXIT"
fi
`, {mode: 0o700});
  for (const name of ['wait-http', 'wait-nodes']) fs.writeFileSync(path.join(scripts, name + '.mjs'), '');
  fs.writeFileSync(path.join(scripts, 'prepare-e2e.mjs'), `
    import {writeFileSync} from 'node:fs';
    import {join} from 'node:path';
    const save = (name, value) => writeFileSync(join(process.env.WEBAPI_E2E_DIRECTORY, name), value);
    const created = ${JSON.stringify(actors.slice(0, rotated))}.map(actor => 'password-e2e-' + actor);
    for (const name of created) save(name, 'synthetic');
    save('created.json', JSON.stringify(created));
    save('password-e2e-unrelated', 'unrelated fixture file');
    if (created.length < 4) process.exit(${runExit});
    save('credential', 'synthetic');
    save('context.json', JSON.stringify({environmentId: 'fixture'}));
  `);
  fs.writeFileSync(path.join(scripts, 'e2e-faults.mjs'), `process.exit(${runExit});`);
  execFileSync('git', ['init', '-q'], {cwd: root});
  execFileSync('git', ['-c', 'user.name=fixture', '-c', 'user.email=fixture@localhost',
    'commit', '-qm', 'fixture', '--allow-empty'], {cwd: root});

  const result = spawnSync('bash', ['scripts/e2e.sh'], {cwd: root, encoding: 'utf8', env: {
    ...process.env, PATH: bin + path.delimiter + process.env.PATH, WEBAPI_NODE: process.execPath,
    FIXTURE_DOWN_EXIT: String(downExit),
  }});
  assert.ifError(result.error);
  const runs = fs.readdirSync(fixtures).filter(name => name !== 'other-project');
  assert.equal(runs.length, 1);
  const run = path.join(fixtures, runs[0]);
  const created = JSON.parse(fs.readFileSync(path.join(run, 'created.json'), 'utf8'));
  assert.equal(created.length, rotated);
  assert.equal(fs.existsSync(path.join(run, 'down-attempted')), true);
  assert.deepEqual([...bootstrapFiles, 'credential', ...created].filter(name => fs.existsSync(path.join(run, name))), []);
  assert.equal(fs.readFileSync(path.join(run, 'password-e2e-unrelated'), 'utf8'), 'unrelated fixture file');
  assert.equal(fs.readFileSync(path.join(other, 'password-e2e-admin'), 'utf8'), 'other fixture');
  assert.equal(result.status, runExit || downExit, result.stderr);
}

test('successful core E2E removes all four rotated passwords', t => runFixture(t, {}));
test('partial first password changes are cleaned on setup failure', t => runFixture(t, {rotated: 2, runExit: 37}));
test('a failed run keeps its exit code and cleans passwords even if Compose down fails',
  t => runFixture(t, {runExit: 23, downExit: 19}));
test('a successful run cleans passwords and reports a Compose down failure', t => runFixture(t, {downExit: 19}));
