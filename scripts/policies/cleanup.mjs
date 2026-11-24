import path from 'node:path';
import assert from 'node:assert/strict';
import {cleanupAcceptanceRun} from '../acceptance-credentials.mjs';

export const secretNames = ['password', 'postgres-password', 'node-a', 'node-b', 'ip-hmac', 'cursor-key', 'login-hmac'];
const cleanupNames = [...secretNames, 'original.snapshot', 'rollback.snapshot'];

export function policyComposeArguments(directory, project) {
  const deploy = path.join(directory, 'build/source/deploy');
  return ['compose', '-p', project, '-f', path.join(deploy, 'compose.e2e.yml'),
    '-f', path.join(deploy, 'compose.observability.yml'), '-f', path.join(deploy, 'compose.observability-e2e.yml'),
    '-f', path.join(directory, 'policy-overlay.yml'), '-f', path.join(directory, 'policy-volumes.yml')];
}

export function cleanupPolicyRun(directory, prepareTeardown) {
  return cleanupAcceptanceRun(directory, cleanupNames, async () => {
    const {project, owner, args, docker} = await prepareTeardown();
    if (!args) return {};
    assert.deepEqual(args, policyComposeArguments(directory, project), 'Owned policy Compose arguments mismatch');
    const filter = ['--filter', 'label=com.docker.compose.project=' + project];
    const resources = [
      {count: 'containersRemaining', list: ['ps', '-aq'], inspect: ['inspect', '--format', '{{json .Config.Labels}}']},
      {count: 'volumesRemaining', list: ['volume', 'ls', '-q'], inspect: ['volume', 'inspect', '--format', '{{json .Labels}}']},
    ];
    const list = async resource => (await docker([...resource.list, ...filter])).split('\n').filter(Boolean);
    for (const resource of resources) {
      for (const id of await list(resource)) {
        const labels = JSON.parse(await docker([...resource.inspect, id]));
        assert.equal(labels['com.docker.compose.project'], project);
        assert.equal(labels['com.webapi.policies.owner'], owner);
      }
    }
    await docker([...args, 'down', '--volumes', '--remove-orphans']);
    const receipt = {};
    for (const resource of resources) {
      try {
        receipt[resource.count] = (await list(resource)).length;
        if (receipt[resource.count]) {
          receipt.failed = true;
          receipt.error = 'Owned policy resources remain';
        }
      } catch {
        receipt.failed = true;
        receipt.error = 'Owned policy resource count unavailable';
      }
    }
    return receipt;
  });
}
