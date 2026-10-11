import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {docker} from '../../scripts/runtime/docker.mjs';

test('docker preserves UTF-8 characters split across stdout and stderr chunks', async t => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'webapi-docker-stream-'));
  t.after(() => fs.rm(directory, {recursive: true, force: true}));
  const writer = path.join(directory, 'writer.mjs');
  await fs.writeFile(writer, `import fs from 'node:fs';
const stdout = Buffer.from('节点状态更新');
const stderr = Buffer.from('源文档平台功能权限');
fs.writeSync(1, stdout.subarray(0, 1));
fs.writeSync(2, stderr.subarray(0, 2));
setTimeout(() => {
  fs.writeSync(1, stdout.subarray(1));
  fs.writeSync(2, stderr.subarray(2));
}, 250);
`);
  const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
  await fs.writeFile(path.join(directory, 'docker'),
    '#!/bin/sh\nexec ' + quote(process.execPath) + ' ' + quote(writer) + '\n', {mode: 0o700});

  const result = await docker([], {env: {...process.env, PATH: directory + path.delimiter + process.env.PATH}});
  assert.deepEqual(result, {stdout: '节点状态更新', stderr: '源文档平台功能权限', exitCode: 0});
});
